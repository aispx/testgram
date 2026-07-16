using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stats;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/stats">statistics</a> for a certain <a href="https://corefork.telegram.org/api/stories">story</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORIES_NEVER_CREATED This peer hasn't ever posted any stories.
/// <para><c>See <a href="https://corefork.telegram.org/method/stats.getStoryStats"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStoryStatsHandler(
    IStatsAccessController accessController,
    IStatsService statsService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stats.RequestGetStoryStats, MyTelegram.Schema.Stats.IStoryStats>
{
    protected override async Task<MyTelegram.Schema.Stats.IStoryStats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stats.RequestGetStoryStats obj)
    {
        var peer = await accessController.ResolvePeerForStoryStatsAsync(input, obj.Peer);

        return await statsService.GetStoryStatsAsync(input, peer, obj.Id, obj.Dark);
    }
}
