// Feature: push-updates, Property 26: message deletion notification.
//
// For any non-empty list of deleted message ids (and any peer / recipient), the production
// MessagePushDataBuilder.BuildMessageDeleted(recipientUserId, peer, messageIds) produces a PushData
// whose loc_key is exactly PushNotificationTypes.MessageDeleted and whose custom.messages is the
// comma-joined list of the deleted ids (string.Join(",", ids)). This drives the real builder over
// generated non-empty int id lists and the task-1 peer generator (User/Chat/Channel) plus pooled
// recipient user ids. The builder depends on IUserAppService only to resolve display names (not used
// by this service/cancel builder), so a minimal stub is supplied.
//
// Validates: Requirements 8.1

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Push.Tests;

public class Property26_MessageDeletedTests
{
    /// <summary>
    /// A non-empty list of deleted message ids paired with an arbitrary peer (User/Chat/Channel, reused
    /// from the task-1 generator) and a recipient user id. Ids are drawn over a wide int range so the
    /// comma-join is exercised across single/multi/duplicate values.
    /// </summary>
    private static Gen<(long Recipient, Peer Peer, int[] Ids)> DeletedMessagesCase =>
        from recipient in PushGen.PooledUserId
        from peer in PushGen.AnyPeer
        from count in Gen.Choose(1, 8)
        from ids in GenHelpers.ArrayOfLength(count, Gen.Choose(1, 1_000_000))
        select (recipient, peer, ids);

    // Property 26: message deletion notification
    // Validates: Requirements 8.1
    [Property(MaxTest = 100)]
    public Property MessageDeleted_sets_loc_key_and_comma_joined_messages()
    {
        return Prop.ForAll(Arb.From(DeletedMessagesCase), input =>
        {
            var (recipient, peer, ids) = input;
            var builder = new MessagePushDataBuilder(new StubUserAppService());

            var push = builder.BuildMessageDeleted(recipient, peer, ids);

            var expectedMessages = string.Join(",", ids);
            var ok = push.LocKey == PushNotificationTypes.MessageDeleted &&
                     push.Custom is not null &&
                     push.Custom!.Messages == expectedMessages &&
                     push.UserId == recipient;

            return ok.Label(
                $"loc_key='{push.LocKey}' (exp '{PushNotificationTypes.MessageDeleted}'), " +
                $"messages='{push.Custom?.Messages}' (exp '{expectedMessages}'), " +
                $"user_id={push.UserId} (exp {recipient}), peer={peer.PeerType}");
        });
    }

    /// <summary>
    /// Minimal <see cref="IUserAppService"/> stub. <c>BuildMessageDeleted</c> does not resolve any user,
    /// but the builder constructor requires this dependency; every member returns an empty/absent result.
    /// </summary>
    private sealed class StubUserAppService : IUserAppService
    {
        public Task<IUserReadModel?> GetAsync(long? id) => Task.FromResult<IUserReadModel?>(null);

        public Task<IUserReadModel> GetAsync(long id) => Task.FromResult<IUserReadModel>(null!);

        public Task<IReadOnlyCollection<IUserReadModel>> GetListAsync(IEnumerable<long> ids) =>
            Task.FromResult<IReadOnlyCollection<IUserReadModel>>(Array.Empty<IUserReadModel>());

        public Task CheckAccountPremiumStatusAsync(long userId) => Task.CompletedTask;

        public Task<IUserFullReadModel?> GetUserFullAsync(long userId) =>
            Task.FromResult<IUserFullReadModel?>(null);

        public void InvalidateCache(long userId)
        {
        }
    }
}
