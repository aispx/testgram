using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 11: custom carries the event identifiers according to the chat type.
//
// For any built new-message notification, custom.msg_id equals the message id, and depending on the
// peer type exactly the corresponding identifier is set. This exercises the production
// MessagePushDataBuilder over an arbitrary MessageItem fixture (reusing the task-1 MessageCase
// generator across User/Channel peers):
//   * personal (user-to-user) messages are built with BuildForPersonalMessageAsync, which sets
//     custom.from_id to the sender and leaves the channel/chat identifiers unset;
//   * channel/supergroup messages are built with BuildForChannelMessageAsync, which sets
//     custom.channel_id to the channel peer id.
// In both cases custom.msg_id must equal the originating message id.
//
// Validates: Requirements 4.3
public class Property11_CustomEventIdsTests
{
    /// <summary>
    /// New-message fixtures only (Text/Media/Reaction) over the two peer types whose builder sets an
    /// unambiguous identifier: User (personal -> from_id) and Channel (channel -> channel_id). Calls
    /// are service notifications, not new messages, so they are excluded.
    /// </summary>
    private static Gen<MessageCase> NewMessageCase =>
        PushGen.MessageCase.Where(mc =>
            mc.Kind != MessageKind.Call &&
            (mc.PeerType == PeerType.User || mc.PeerType == PeerType.Channel));

    // Property 11: custom carries the event identifiers according to the chat type
    // Validates: Requirements 4.3
    [Property(MaxTest = 100)]
    public Property Custom_carries_msgId_and_the_peer_identifier_for_its_chat_type()
    {
        return Prop.ForAll(Arb.From(NewMessageCase), mc =>
        {
            var builder = new MessagePushDataBuilder(new StubUserAppService());
            var item = mc.Item;

            if (mc.PeerType == PeerType.User)
            {
                // Personal (1:1) message: the builder sets from_id to the sender, msg_id to the message
                // id, and leaves chat/channel identifiers unset.
                var push = builder.BuildForPersonalMessageAsync(item).GetAwaiter().GetResult();
                var custom = push!.Custom!;

                var ok = custom.MsgId == item.MessageId &&
                         custom.FromId == item.SenderUserId &&
                         custom.ChannelId is null &&
                         custom.ChatId is null;

                return ok.Label(
                    $"personal: msgId={custom.MsgId} (exp {item.MessageId}), " +
                    $"fromId={custom.FromId} (exp {item.SenderUserId}), " +
                    $"channelId={custom.ChannelId?.ToString() ?? "null"}, chatId={custom.ChatId?.ToString() ?? "null"}");
            }

            // Channel / supergroup message: the builder sets channel_id to the channel peer id and
            // msg_id to the message id.
            var channelPush = builder.BuildForChannelMessageAsync(item, "Chat").GetAwaiter().GetResult();
            var channelCustom = channelPush!.Custom!;

            var channelOk = channelCustom.MsgId == item.MessageId &&
                            channelCustom.ChannelId == item.ToPeer.PeerId;

            return channelOk.Label(
                $"channel: msgId={channelCustom.MsgId} (exp {item.MessageId}), " +
                $"channelId={channelCustom.ChannelId?.ToString() ?? "null"} (exp {item.ToPeer.PeerId})");
        });
    }

    /// <summary>
    /// Minimal <see cref="IUserAppService"/> stub: the builder only resolves the sender display name
    /// via <see cref="GetAsync(long)"/> (and falls back to "Unknown" on a null/throwing result), which
    /// has no bearing on the identifiers asserted by this property.
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

        public void InvalidateCache(long userId) { }
    }
}
