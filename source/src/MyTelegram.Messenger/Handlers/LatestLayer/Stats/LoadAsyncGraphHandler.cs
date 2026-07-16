using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stats;
/// <summary>
/// Load <a href="https://corefork.telegram.org/api/stats">channel statistics graph</a> asynchronously
/// Possible errors
/// Code Type Description
/// 400 GRAPH_EXPIRED_RELOAD This graph has expired, please obtain a new graph token.
/// 400 GRAPH_INVALID_RELOAD Invalid graph token provided, please reload the stats and provide the updated token.
/// 400 GRAPH_OUTDATED_RELOAD The graph is outdated, please get a new async token using stats.getBroadcastStats.
/// <para><c>See <a href="https://corefork.telegram.org/method/stats.loadAsyncGraph"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class LoadAsyncGraphHandler(IStatsService statsService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stats.RequestLoadAsyncGraph, MyTelegram.Schema.IStatsGraph>
{
    protected override async Task<MyTelegram.Schema.IStatsGraph> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stats.RequestLoadAsyncGraph obj)
    {
        // No channel resolution: the token itself scopes the request. Token/zoom resolution and the
        // GRAPH_*_RELOAD error mapping are handled by the Stats_Service (Requirements 9.2-9.7).
        return await statsService.LoadAsyncGraphAsync(input, obj.Token, obj.X);
    }
}
