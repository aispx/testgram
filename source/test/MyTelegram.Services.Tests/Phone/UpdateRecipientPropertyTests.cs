using System.Reflection;
using CsCheck;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based test for design Property 12 (Update recipient completeness).
///
/// <para><b>Property 12:</b> any state-changing call operation delivers the corresponding update to all
/// other active sessions of every current subscriber (participants / creator / invited + the call peer),
/// excluding the originating device.</para>
///
/// The property drives the real group-call handlers over an in-memory Mongo store for a
/// randomly-generated call topology (a variable number of joined participants, a creator, a set of invited
/// users, and either a user-peer or a channel-peer call). It then applies a single genuine state change
/// - either <c>EditGroupCallParticipantHandler</c> (mutes a participant, emitting
/// <c>updateGroupCallParticipants</c>) or <c>ToggleGroupCallSettingsHandler</c> (flips <c>join_muted</c>,
/// emitting <c>updateGroupCall</c>) - initiated by one of the participants (the originator).
///
/// After the operation it asserts, against an independently-computed oracle of the expected subscriber
/// set:
///   * the set of user ids the update was delivered to equals exactly the current subscribers minus the
///     originator (so no subscriber is missed and none is invented);
///   * the originating user receives no push at all (its own device / sessions are excluded);
///   * every expected recipient actually receives a push that carries the state-changing update; and
///   * for a channel-peer call the update is also fanned out to the channel peer with the originator
///     carried as <c>excludeUserId</c> so the originating device on that peer is excluded too.
///
/// Delivering to a user <em>peer</em> (rather than a single session) is how the dispatcher reaches all of
/// that user's active authorized sessions (Requirement 30.1); excluding the originator from the user
/// fan-out and carrying it as <c>excludeUserId</c> on the peer fan-out is how the originating device is
/// left out.
///
/// <b>Validates: Requirements 30.1</b>
/// </summary>
public class UpdateRecipientPropertyTests
{
    private const long CallId = 900;
    private const long CreatorId = 1;
    private const long AccessHash = 99999;
    private const long ChannelPeerId = 500;

    // Disjoint id pools so participants / creator / invited never accidentally coincide.
    private const long ParticipantBase = 10; // 10..15
    private const long InvitedBase = 20;     // 20..22

    /// <summary>The randomly-generated shape of a call and the operation applied to it.</summary>
    private sealed record Topology(
        int ParticipantCount,
        int InvitedCount,
        bool PeerIsUser,
        bool UseToggle,
        int OriginatorIndex);

    [Fact]
    public void StateChange_DeliversUpdateToAllOtherSubscribers_ExcludingOriginator()
    {
        var topologyGen =
            from participantCount in Gen.Int[2, 6]
            from invitedCount in Gen.Int[0, 3]
            from peerIsUser in Gen.Bool
            from useToggle in Gen.Bool
            from originatorIndex in Gen.Int[0, participantCount - 1]
            select new Topology(participantCount, invitedCount, peerIsUser, useToggle, originatorIndex);

        topologyGen.Sample(topology => RunScenario(topology), iter: 200);
    }

    // A deterministic example: a channel-peer call with several participants and invited users, edited by a
    // mid-list participant - exercises the channel-peer fan-out (excludeUserId) and invited-user delivery.
    [Fact]
    public void EditParticipant_OnChannelCall_FansOutToEveryoneButOriginator()
    {
        RunScenario(new Topology(
            ParticipantCount: 4,
            InvitedCount: 2,
            PeerIsUser: false,
            UseToggle: false,
            OriginatorIndex: 1));
    }

    // A deterministic example: toggling settings on a user-peer call reaches the creator + participants +
    // invited, but never the originating participant.
    [Fact]
    public void ToggleSettings_OnUserCall_FansOutToEveryoneButOriginator()
    {
        RunScenario(new Topology(
            ParticipantCount: 3,
            InvitedCount: 1,
            PeerIsUser: true,
            UseToggle: true,
            OriginatorIndex: 2));
    }

    private static void RunScenario(Topology topology)
    {
        RunScenarioAsync(topology).GetAwaiter().GetResult();
    }

    private static async Task RunScenarioAsync(Topology topology)
    {
        var participantUserIds = Enumerable
            .Range(0, topology.ParticipantCount)
            .Select(i => ParticipantBase + i)
            .ToList();
        var invitedUserIds = Enumerable
            .Range(0, topology.InvitedCount)
            .Select(i => InvitedBase + i)
            .ToList();
        // Both operations under test mutate shared call state, which requires manage-call rights.
        // On a channel-peer call a participant can hold them (as a channel admin), so the creator
        // stays a distinct subscriber; on a user-peer call only the creator can, so it originates.
        var originatorUserId = topology.PeerIsUser
            ? CreatorId
            : participantUserIds[topology.OriginatorIndex];

        var database = PhoneTestFixtures.CreateDatabase(out _);
        SeedGroupCall(database, participantUserIds, invitedUserIds, topology.PeerIsUser);
        var sender = new CapturingObjectMessageSender();

        // Independently-computed oracle of the subscribers who must receive the update. Mirrors the
        // requirement ("participants / creator / invited + peer") rather than reusing production code, so a
        // regression in the fan-out helper is actually caught.
        var expectedRecipientUserIds = participantUserIds
            .Append(CreatorId)
            .Concat(invitedUserIds)
            .Where(id => id != originatorUserId)
            .Distinct()
            .ToHashSet();

        IUpdates updates;
        if (topology.UseToggle)
        {
            // Seed join_muted = false, flip it to true -> a genuine state change emitting updateGroupCall.
            updates = await InvokeAsync("ToggleGroupCallSettingsHandler", database, sender, originatorUserId,
                new RequestToggleGroupCallSettings { Call = InputCall(), JoinMuted = true });
        }
        else
        {
            // Mute a participant other than the originator -> a genuine state change emitting
            // updateGroupCallParticipants. (Targeting a distinct participant avoids the input peer being
            // resolved to a "self" peer, which would not match the stored user participant entry.)
            var targetUserId = participantUserIds.First(id => id != originatorUserId);
            updates = await InvokeAsync("EditGroupCallParticipantHandler", database, sender, originatorUserId,
                new RequestEditGroupCallParticipant
                {
                    Call = InputCall(),
                    Participant = new TInputPeerUser { UserId = targetUserId, AccessHash = 0 },
                    Muted = true
                });
        }

        // Sanity: the operation actually produced a group-call update to fan out.
        CarriesGroupCallUpdate(updates).ShouldBeTrue("the operation did not emit a group-call update");

        // Property 12: the update is delivered to exactly the current subscribers, minus the originator -
        // no subscriber is missed and no extra recipient is invented.
        var actualUserRecipients = sender.TargetUserIds.ToHashSet();
        actualUserRecipients.SetEquals(expectedRecipientUserIds).ShouldBeTrue(
            $"delivered user set {{{string.Join(",", actualUserRecipients.OrderBy(x => x))}}} != " +
            $"expected {{{string.Join(",", expectedRecipientUserIds.OrderBy(x => x))}}} " +
            $"(originator {originatorUserId}, peerIsUser={topology.PeerIsUser}, toggle={topology.UseToggle})");

        // Property 12: the originating user's own sessions are excluded from the fan-out entirely.
        sender.PushesToUser(originatorUserId).ShouldBeEmpty(
            $"the originating user {originatorUserId} must not receive the update");

        // Property 12: every expected subscriber actually receives a push carrying the update (delivering to
        // the user peer reaches all of that user's active sessions - Requirement 30.1).
        foreach (var recipient in expectedRecipientUserIds)
        {
            sender.PushesToUser(recipient).ShouldContain(p => CarriesGroupCallUpdate(p.Data),
                $"subscriber {recipient} did not receive the state-changing update");
        }

        // Property 12: for a channel-peer call the update is also fanned out to the peer, with the
        // originator carried as excludeUserId so the originating device on that peer is excluded.
        if (!topology.PeerIsUser)
        {
            var peerPushes = sender.Pushes
                .Where(p => p.Peer.PeerType == PeerType.Channel && p.Peer.PeerId == ChannelPeerId)
                .ToList();
            peerPushes.ShouldContain(p => CarriesGroupCallUpdate(p.Data) && p.ExcludeUserId == originatorUserId,
                "the channel peer did not receive the update with the originator excluded");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IInputGroupCall InputCall() => new TInputGroupCall { Id = CallId, AccessHash = AccessHash };

    private static bool CarriesGroupCallUpdate(IObject data)
    {
        var updates = data switch
        {
            TUpdates u => u.Updates.ToList(),
            _ => new List<IUpdate>()
        };

        return updates.Any(u => u is TUpdateGroupCall or TUpdateGroupCallParticipants);
    }

    private static bool CarriesGroupCallUpdate(IUpdates updates)
    {
        return updates is TUpdates tUpdates &&
               tUpdates.Updates.Any(u => u is TUpdateGroupCall or TUpdateGroupCallParticipants);
    }

    private static void SeedGroupCall(
        IMongoDatabase database,
        IEnumerable<long> participantUserIds,
        IReadOnlyList<long> invitedUserIds,
        bool peerIsUser)
    {
        var source = 1000;
        var participants = participantUserIds
            .Select(userId => new GroupCallParticipantDoc
            {
                UserId = userId,
                PeerId = userId,
                PeerType = (int)PeerType.User,
                Source = source++
            })
            .ToList();

        var peerType = peerIsUser ? PeerType.User : PeerType.Channel;
        var peerId = peerIsUser ? CreatorId : ChannelPeerId;

        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        collection.InsertOne(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = CreatorId,
            PeerId = peerId,
            PeerType = (int)peerType,
            Active = true,
            JoinMuted = false,
            Version = 1,
            InvitedUserIds = invitedUserIds.ToList(),
            Participants = participants
        });
    }

    private static async Task<IUpdates> InvokeAsync(
        string handlerTypeName,
        IMongoDatabase database,
        IObjectMessageSender sender,
        long inputUserId,
        IObject request)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        var handler = PhoneTestFixtures.CreateGroupCallHandler(type, database, sender);
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        var input = PhoneTestFixtures.RequestInput(inputUserId).Build();
        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = await (Task<IObject>)taskObj;
        var rpcResult = (TRpcResult)result;
        return (IUpdates)rpcResult.Result;
    }
}
