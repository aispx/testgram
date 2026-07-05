// Feature: push-updates, Property 27: Уведомление о прочтении истории (read-history cancel notification).
//
// For any maxId value (and any recipient/peer), MessagePushDataBuilder.BuildReadHistory produces a
// service push with loc_key = READ_HISTORY and custom.max_id == maxId. This test drives the real
// builder over an arbitrary maxId (any int, including 0 and negatives) and the task-1 peer/user-id
// generators, then asserts the loc_key and that custom.max_id is carried verbatim.
//
// Validates: Requirements 8.2

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property27_ReadHistoryTests
{
    /// <summary>
    /// A read-history fixture: a recipient account id (reused task-1 pooled-user generator), an
    /// arbitrary peer (User/Chat/Channel, task-1 <see cref="PushGen.AnyPeer"/>) and an arbitrary
    /// <c>maxId</c> spanning the full int range so 0 and negative edge values are exercised.
    /// </summary>
    private static Gen<(long RecipientUserId, Peer Peer, int MaxId)> ReadHistory =>
        from recipientUserId in PushGen.PooledUserId
        from peer in PushGen.AnyPeer
        from maxId in Arb.Generate<int>()
        select (recipientUserId, peer, maxId);

    // Property 27: Уведомление о прочтении истории
    // Validates: Requirements 8.2
    [Property(MaxTest = 100)]
    public Property Read_history_push_sets_lockey_and_max_id()
    {
        // BuildReadHistory is a pure function and does not consult the user app service, so a null
        // dependency is sufficient.
        var builder = new MessagePushDataBuilder(userAppService: null!);

        return Prop.ForAll(Arb.From(ReadHistory), input =>
        {
            var (recipientUserId, peer, maxId) = input;

            PushData data = builder.BuildReadHistory(recipientUserId, peer, maxId);

            data.LocKey.ShouldBe(PushNotificationTypes.ReadHistory);
            data.UserId.ShouldBe(recipientUserId);

            data.Custom.ShouldNotBeNull();
            data.Custom!.MaxId.ShouldBe(maxId);

            return true;
        });
    }
}
