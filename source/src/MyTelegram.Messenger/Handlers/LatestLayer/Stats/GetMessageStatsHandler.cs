using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stats;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/stats">message statistics</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stats.getMessageStats"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMessageStatsHandler(
    IStatsAccessController accessController,
    IStatsService statsService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stats.RequestGetMessageStats, MyTelegram.Schema.Stats.IMessageStats>
{
    protected override async Task<MyTelegram.Schema.Stats.IMessageStats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stats.RequestGetMessageStats obj)
    {
        var channel = await accessController.ResolveChannelForStatsAsync(
            input, obj.Channel, StatsChannelKind.Any, checkJoinable: false);

        return await statsService.GetMessageStatsAsync(input, channel.ChannelId, obj.MsgId, obj.Dark);
    }
}
