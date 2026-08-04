using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Check if the specified <a href="https://corefork.telegram.org/api/search#posts-tab">global post search »</a> requires payment.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.checkSearchPostsFlood"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckSearchPostsFloodHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestCheckSearchPostsFlood, MyTelegram.Schema.ISearchPostsFlood>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.ISearchPostsFlood> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestCheckSearchPostsFlood obj)
    {
        var state = await SearchPostsFloodHelper.GetStateAsync(mongoDatabase, input.UserId);

        return new TSearchPostsFlood
        {
            QueryIsFree = state.QueryIsFree,
            Remains = state.Remains,
            TotalDaily = SearchPostsFloodHelper.TotalDaily,
            StarsAmount = SearchPostsFloodHelper.StarsAmount,
            WaitTill = state.WaitTill > 0 ? state.WaitTill : null
        };
    }
}
