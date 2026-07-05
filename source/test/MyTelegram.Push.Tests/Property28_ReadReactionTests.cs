// Feature: push-updates, Property 28: Уведомление о прочтении реакций (read-reaction service push)
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Schema;
using Shouldly;

namespace MyTelegram.Push.Tests;

/// <summary>
/// Property 28: Уведомление о прочтении реакций.
///
/// <para>
/// For any non-empty list of message ids and any peer, the service-push builder
/// (<see cref="MessagePushDataBuilder.BuildReadReaction"/>) produces <c>loc_key = READ_REACTION</c>
/// and a <c>custom.messages</c> string that carries exactly those ids as a comma-separated list
/// (<c>string.Join(",", ids)</c>).
/// </para>
///
/// Validates: Requirements 8.3
/// </summary>
public class Property28_ReadReactionTests
{
    // BuildReadReaction is a pure function and never touches the IUserAppService dependency, so a null
    // service is sufficient for exercising it directly (matching the task-1 reaction-test convention).
    private static readonly MessagePushDataBuilder Builder = new(null!);

    /// <summary>Non-empty list (1..8 entries) of positive message ids, reusing the task-1 id pool.</summary>
    private static Gen<List<int>> NonEmptyMessageIds =>
        from n in Gen.Choose(1, 8)
        from ids in GenHelpers.ArrayOfLength(n, Gen.Choose(1, 100000))
        select ids.ToList();

    // Property 28: Уведомление о прочтении реакций
    // Validates: Requirements 8.3
    [Property(MaxTest = 100)]
    public Property ReadReaction_uses_READ_REACTION_loc_key_and_carries_all_message_ids()
    {
        // Reuse the task-1 peer generator (User/Chat/Channel) and pooled recipient ids.
        return Prop.ForAll(
            Arb.From(PushGen.AnyPeer),
            Arb.From(PushGen.PooledUserId),
            Arb.From(NonEmptyMessageIds),
            (Peer peer, long recipientUserId, List<int> messageIds) =>
            {
                var push = Builder.BuildReadReaction(recipientUserId, peer, messageIds);

                // loc_key is the READ_REACTION service-cancel key.
                push.LocKey.ShouldBe(PushNotificationTypes.ReadReaction);

                // custom.messages contains exactly the supplied ids as a comma-separated list.
                push.Custom.ShouldNotBeNull();
                push.Custom!.Messages.ShouldBe(string.Join(",", messageIds));

                // user_id is the recipient account.
                push.UserId.ShouldBe(recipientUserId);
            });
    }
}
