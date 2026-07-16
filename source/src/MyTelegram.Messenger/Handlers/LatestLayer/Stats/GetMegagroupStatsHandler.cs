using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stats;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/stats">supergroup statistics</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 MEGAGROUP_REQUIRED You can only use this method on a supergroup.
/// <para><c>See <a href="https://corefork.telegram.org/method/stats.getMegagroupStats"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMegagroupStatsHandler(
    IStatsAccessController accessController,
    IStatsService statsService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stats.RequestGetMegagroupStats, MyTelegram.Schema.Stats.IMegagroupStats>
{
    protected override async Task<MyTelegram.Schema.Stats.IMegagroupStats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stats.RequestGetMegagroupStats obj)
    {
        var channel = await accessController.ResolveChannelForStatsAsync(
            input, obj.Channel, StatsChannelKind.MegagroupOnly, checkJoinable: false);

        return await statsService.GetMegagroupStatsAsync(input, channel.ChannelId, obj.Dark);
    }
}
