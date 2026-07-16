using EventFlow.Queries;
using Moq;
using MyTelegram;
using MyTelegram.Abstractions;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using FsCheck.Xunit;
using MongoDB.Driver;
using StatsSchema = MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 9: Referenced chats and users are included in the response.
///
/// For any returned <c>publicForwards</c> page, every chat and user entity referenced by the
/// <c>forwards</c> list appears in the response's <c>chats</c> or <c>users</c> list.
///
/// Validates: Requirements 6.4, 7.4.
///
/// <para>This drives the real production code path — the concrete <see cref="StatsService"/> and its
/// private <c>BuildPublicForwardsAsync</c> assembly — rather than a re-implementation. Both
/// <c>GetMessagePublicForwardsAsync</c> (Requirement 6.4) and <c>GetStoryPublicForwardsAsync</c>
/// (Requirement 7.4) funnel through the same <c>BuildPublicForwardsAsync</c>; the message method is
/// exercised here because it needs no story document store, and it covers the shared entity-collection
/// logic in full.</para>
///
/// <para>The service's many collaborators are faithful stubs/mocks that model the documented contract:</para>
/// <list type="bullet">
///   <item><see cref="IPublicForwardStore"/> returns a controlled page derived from the shared
///   <see cref="StatsGen.ForwardEventSequence"/> generator (deduped, public-only, non-removed set).</item>
///   <item><see cref="IQueryProcessor"/> resolves each recorded forward's <c>(forwardingPeerId, msgId)</c>
///   to a distinct message read model (and the source message for the existence check).</item>
///   <item><see cref="IMessageConverterService.ToMessage"/> produces a message that references its
///   forwarding channel (as <c>PeerId</c>), a sender user (as <c>FromId</c>), and a forwarded-from channel
///   (as <c>FwdFrom.FromId</c>).</item>
///   <item><see cref="IMessageAppService.GetExtraPeerIds"/> extracts the sender user and forwarded-from
///   channel from the read models — mirroring how the real service derives referenced peers — while the
///   forwarding channel itself is supplied by <c>BuildPublicForwardsAsync</c>'s forwarding-peer loop.</item>
///   <item><see cref="IChatConverterService.GetChannelListAsync"/> /
///   <see cref="IUserConverterService.GetUserListAsync"/> echo back one entity per requested id, so the
///   response's <c>chats</c>/<c>users</c> contain exactly the ids the service asked to resolve.</item>
/// </list>
///
/// <para>The expected "referenced" set is read back from the produced <c>forwards</c> messages (their
/// <c>PeerId</c>, <c>FromId</c>, and <c>FwdFrom.FromId</c>), not re-derived from the inputs, so the
/// property genuinely checks that the service collected and resolved every referenced entity across all
/// three collection paths (forwarding-peer loop, extra-peer users, extra-peer channels). Each run executes
/// a minimum of 100 generated cases.</para>
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class ReferencedChatsUsersInclusionPropertyTests
{
    private const int MaxPageItems = 100;

    [Property]
    public void Referenced_chats_and_users_are_included_in_the_response(ForwardEventSequenceFixture sequence)
    {
        // The source message whose public forwards are requested. Owner ids come from a disjoint pool
        // (~1001..1020) from the forwarding peers (~5001..5008), so the existence-check query never
        // collides with a forward-resolution query.
        var sourceOwnerId = sequence.SourceOwnerPeerId;
        var sourceMsgId = (int)sequence.SourceItemId;

        // Derive the deduped, public-only, non-removed set the store would hold, in its total order.
        var liveOrdered = ComputeExpectedOrdered(sequence.Events);
        var pageItems = liveOrdered.Take(MaxPageItems).ToList();

        // ---- Build the read-model resolution maps ----------------------------------------------
        // Each forward's (forwardingPeerId, msgId) resolves to a distinct read model; a reference-keyed
        // map records which forward each read model belongs to so ToMessage / GetExtraPeerIds can produce
        // consistent references (as the real converter + app service would from a real read model).
        var readModelByCoords = new Dictionary<(long PeerId, int MsgId), IMessageReadModel>();
        var forwardByReadModel = new Dictionary<IMessageReadModel, (long ForwardingPeerId, int MsgId)>(
            ReferenceEqualityComparer.Instance);

        // The source existence read model is not part of the forwards, so it carries no references.
        readModelByCoords[(sourceOwnerId, sourceMsgId)] = Mock.Of<IMessageReadModel>();

        foreach (var record in pageItems)
        {
            var coords = (record.ForwardingPeerId, record.ForwardingMsgId);
            if (readModelByCoords.ContainsKey(coords))
            {
                continue;
            }

            var readModel = Mock.Of<IMessageReadModel>();
            readModelByCoords[coords] = readModel;
            forwardByReadModel[readModel] = coords;
        }

        var service = CreateService(sourceOwnerId, sourceMsgId, pageItems, readModelByCoords, forwardByReadModel);

        var input = new TestRequestInput(userId: 777, layer: 195);

        var result = service
            .GetMessagePublicForwardsAsync(input, sourceOwnerId, sourceMsgId, offset: string.Empty, limit: MaxPageItems)
            .GetAwaiter().GetResult();

        var forwards = (StatsSchema.TPublicForwards)result;

        var chatIds = forwards.Chats.Select(c => c.Id).ToHashSet();
        var userIds = forwards.Users.Select(u => u.Id).ToHashSet();

        // For every forward in the returned page, every entity the message references must appear in the
        // response's chats/users lists (Requirements 6.4, 7.4).
        foreach (var forward in forwards.Forwards)
        {
            var message = (TMessage)((TPublicForwardMessage)forward).Message;

            // The forwarding channel the message lives in (collected by the forwarding-peer loop).
            var forwardingChannelId = ((TPeerChannel)message.PeerId).ChannelId;
            chatIds.ShouldContain(forwardingChannelId,
                $"forwarding channel {forwardingChannelId} referenced by a forward is missing from chats");

            // The sender user (collected via GetExtraPeerIds users).
            var senderUserId = ((TPeerUser)message.FromId!).UserId;
            userIds.ShouldContain(senderUserId,
                $"sender user {senderUserId} referenced by a forward is missing from users");

            // The forwarded-from channel (collected via GetExtraPeerIds channels).
            var fwdFromChannelId = ((TPeerChannel)message.FwdFrom!.FromId!).ChannelId;
            chatIds.ShouldContain(fwdFromChannelId,
                $"forwarded-from channel {fwdFromChannelId} referenced by a forward is missing from chats");
        }
    }

    // ---- Deterministic per-forward reference derivations -------------------------------------
    // Distinct id ranges keep forwarding channels (~5001..5008), forwarded-from channels, and sender
    // users disjoint so an assertion failure is unambiguous. cp*100 + m is unique for m in 1..8.

    private static long ForwardedFromChannelId(long forwardingPeerId, int msgId) =>
        6_000_000 + forwardingPeerId * 100 + msgId;

    private static long SenderUserId(long forwardingPeerId, int msgId) =>
        7_000_000 + forwardingPeerId * 100 + msgId;

    private StatsService CreateService(
        long sourceOwnerId,
        int sourceMsgId,
        IReadOnlyList<PublicForwardRecord> pageItems,
        IReadOnlyDictionary<(long PeerId, int MsgId), IMessageReadModel> readModelByCoords,
        IReadOnlyDictionary<IMessageReadModel, (long ForwardingPeerId, int MsgId)> forwardByReadModel)
    {
        // Public forward store: returns the controlled page for the message source.
        var publicForwardStore = new Mock<IPublicForwardStore>(MockBehavior.Loose);
        publicForwardStore
            .Setup(x => x.GetPageAsync(It.IsAny<ForwardSourceKey>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new PublicForwardPage(pageItems.Count, pageItems, null));

        // Query processor: resolves the source existence check and each forward's message read model.
        var queryProcessor = new StubQueryProcessor(readModelByCoords);

        // Message converter: turns each forward read model into a message that references its forwarding
        // channel (PeerId), sender user (FromId), and forwarded-from channel (FwdFrom.FromId).
        var messageConverter = new Mock<IMessageConverterService>(MockBehavior.Loose);
        messageConverter
            .Setup(x => x.ToMessage(
                It.IsAny<long>(),
                It.IsAny<IMessageReadModel>(),
                It.IsAny<IPollReadModel?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<IUserReactionReadModel>?>(),
                It.IsAny<int>()))
            .Returns((long _, IMessageReadModel readModel, IPollReadModel? __, List<string>? ___,
                List<IUserReactionReadModel>? ____, int _____) =>
            {
                var (forwardingPeerId, msgId) = forwardByReadModel[readModel];
                return new TMessage
                {
                    Id = msgId,
                    PeerId = new TPeerChannel { ChannelId = forwardingPeerId },
                    FromId = new TPeerUser { UserId = SenderUserId(forwardingPeerId, msgId) },
                    FwdFrom = new TMessageFwdHeader
                    {
                        FromId = new TPeerChannel { ChannelId = ForwardedFromChannelId(forwardingPeerId, msgId) }
                    }
                };
            });

        // Message app service: extracts the sender users and forwarded-from channels from the read models,
        // mirroring the real GetExtraPeerIds. The forwarding channels themselves are added by the service's
        // forwarding-peer loop, so they are intentionally NOT returned here.
        var messageAppService = new Mock<IMessageAppService>(MockBehavior.Loose);
        messageAppService
            .Setup(x => x.GetExtraPeerIds(It.IsAny<IReadOnlyCollection<IMessageReadModel>>()))
            .Returns((IReadOnlyCollection<IMessageReadModel> readModels) =>
            {
                var users = new HashSet<long>();
                var channels = new HashSet<long>();
                foreach (var readModel in readModels)
                {
                    if (!forwardByReadModel.TryGetValue(readModel, out var coords))
                    {
                        continue;
                    }

                    users.Add(SenderUserId(coords.ForwardingPeerId, coords.MsgId));
                    channels.Add(ForwardedFromChannelId(coords.ForwardingPeerId, coords.MsgId));
                }

                return (users, channels);
            });

        // Chat converter: echoes back one channel entity per requested id.
        var chatConverter = new Mock<IChatConverterService>(MockBehavior.Loose);
        chatConverter
            .Setup(x => x.GetChannelListAsync(
                It.IsAny<IRequestWithAccessHashKeyId>(),
                It.IsAny<List<long>>(),
                It.IsAny<IReadOnlyCollection<IChannelMemberReadModel>?>(),
                It.IsAny<int>()))
            .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> ids,
                IReadOnlyCollection<IChannelMemberReadModel>? __, int ___) =>
                ids.Select(id => (IChat)new TChannel { Id = id }).ToList());

        // User converter: echoes back one user entity per requested id.
        var userConverter = new Mock<IUserConverterService>(MockBehavior.Loose);
        userConverter
            .Setup(x => x.GetUserListAsync(
                It.IsAny<IRequestWithAccessHashKeyId>(),
                It.IsAny<List<long>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<int>()))
            .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> ids, bool __, bool ___, int ____) =>
                ids.Select(id => (ILayeredUser)new TUser { Id = id }).ToList());

        // The remaining collaborators are not exercised by the message public-forwards path.
        var metricsStore = new Mock<IMetricsStore>(MockBehavior.Loose).Object;
        var graphBuilder = new Mock<IGraphBuilder>(MockBehavior.Loose).Object;
        var asyncGraphStore = new Mock<IAsyncGraphStore>(MockBehavior.Loose).Object;

        return new StatsService(
            metricsStore,
            graphBuilder,
            userConverter.Object,
            chatConverter.Object,
            publicForwardStore.Object,
            asyncGraphStore,
            messageConverter.Object,
            messageAppService.Object,
            queryProcessor,
            // The Mongo database is only used by the story-existence check, never by the message path.
            mongoDatabase: null!,
            StatsTestOptions.Create());
    }

    /// <summary>
    /// Replays the event stream to derive the store's currently-held, non-removed forwards in the
    /// deterministic total order <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c> — deduped on the
    /// forwarding message and limited to public forwarders (Requirements 11.1/11.2/11.5/11.6).
    /// </summary>
    private static List<PublicForwardRecord> ComputeExpectedOrdered(IReadOnlyList<ForwardEventFixture> events)
    {
        var live = new Dictionary<(long PeerId, int MsgId), PublicForwardRecord>();

        foreach (var e in events)
        {
            var key = (e.ForwardingPeerId, e.ForwardingMsgId);
            if (e.Op == ForwardOpFixture.Record && e.ForwardingPeerIsPublic)
            {
                live[key] = new PublicForwardRecord(e.ForwardingPeerId, e.ForwardingMsgId, e.OrderKey);
            }
            else if (e.Op == ForwardOpFixture.Remove)
            {
                live.Remove(key);
            }
        }

        return live.Values
            .OrderBy(r => r.OrderKey)
            .ThenBy(r => r.ForwardingPeerId)
            .ThenBy(r => r.ForwardingMsgId)
            .ToList();
    }

    /// <summary>
    /// A hand-written <see cref="IQueryProcessor"/> that resolves
    /// <see cref="GetMessageByPeerIdAndMessageIdQuery"/> to the pre-built read model for the queried
    /// <c>(peerId, msgId)</c>, or <c>null</c> when none is registered. Only this query type is used by the
    /// public-forwards path.
    /// </summary>
    private sealed class StubQueryProcessor(
        IReadOnlyDictionary<(long PeerId, int MsgId), IMessageReadModel> readModelByCoords) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
        {
            if (query is GetMessageByPeerIdAndMessageIdQuery q)
            {
                readModelByCoords.TryGetValue((q.OwnerPeerId, q.MessageId), out var readModel);
                return Task.FromResult((TResult)(object)readModel!);
            }

            throw new NotSupportedException($"Unexpected query type {query.GetType().Name}");
        }
    }

    /// <summary>
    /// Minimal <see cref="IRequestInput"/> carrying only the user id and layer the public-forwards path
    /// reads; all other members are inert defaults.
    /// </summary>
    private sealed class TestRequestInput(long userId, int layer) : IRequestInput
    {
        public long UserId { get; } = userId;
        public int Layer { get; set; } = layer;
        public string ConnectionId => string.Empty;
        public ConnectionType ConnectionType => default;
        public long AuthKeyId => 0;
        public uint ObjectId { get; set; }
        public long PermAuthKeyId => 0;
        public long ReqMsgId => 0;
        public int SeqNumber => 0;
        public Guid RequestId => Guid.Empty;
        public long Date => 0;
        public DeviceType DeviceType { get; set; }
        public string ClientIp => string.Empty;
        public long SessionId => 0;
        public long AccessHashKeyId { get; set; }
    }
}
