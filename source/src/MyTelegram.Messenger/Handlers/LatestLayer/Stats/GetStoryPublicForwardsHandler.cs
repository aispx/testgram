using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stats;
/// <summary>
/// Obtain forwards of a <a href="https://corefork.telegram.org/api/stories">story</a> as a message to public chats and reposts by public channels.
/// Possible errors
/// Code Type Description
/// 400 OFFSET_INVALID The provided offset is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stats.getStoryPublicForwards"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStoryPublicForwardsHandler(
    IStatsAccessController accessController,
    IStatsService statsService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stats.RequestGetStoryPublicForwards, MyTelegram.Schema.Stats.IPublicForwards>
{
    protected override async Task<MyTelegram.Schema.Stats.IPublicForwards> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stats.RequestGetStoryPublicForwards obj)
    {
        var peer = await accessController.ResolvePeerForStoryStatsAsync(input, obj.Peer);

        try
        {
            return await statsService.GetStoryPublicForwardsAsync(
                input, peer, obj.Id, obj.Offset, obj.Limit);
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
