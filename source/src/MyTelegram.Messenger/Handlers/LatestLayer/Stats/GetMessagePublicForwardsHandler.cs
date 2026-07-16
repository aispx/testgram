using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stats;
/// <summary>
/// Obtains a list of messages, indicating to which other public channels was a channel message forwarded.<br/>
/// Will return a list of <a href="https://corefork.telegram.org/constructor/message">messages</a> with <code>peer_id</code> equal to the public channel to which this message was forwarded.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 OFFSET_INVALID The provided offset is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stats.getMessagePublicForwards"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMessagePublicForwardsHandler(
    IStatsAccessController accessController,
    IStatsService statsService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stats.RequestGetMessagePublicForwards, MyTelegram.Schema.Stats.IPublicForwards>
{
    protected override async Task<MyTelegram.Schema.Stats.IPublicForwards> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stats.RequestGetMessagePublicForwards obj)
    {
        var channel = await accessController.ResolveChannelForStatsAsync(
            input, obj.Channel, StatsChannelKind.Any, checkJoinable: false);

        try
        {
            return await statsService.GetMessagePublicForwardsAsync(
                input, channel.ChannelId, obj.MsgId, obj.Offset, obj.Limit);
        }
        catch (InvalidStatsOffsetException)
        {
            // An unrecognized non-empty pagination cursor is surfaced as an invalid-offset RPC error
            // rather than a partial page (Requirement 6.8).
            RpcErrors.RpcErrors400.OffsetInvalid.ThrowRpcError();
            throw;
        }
    }
}
