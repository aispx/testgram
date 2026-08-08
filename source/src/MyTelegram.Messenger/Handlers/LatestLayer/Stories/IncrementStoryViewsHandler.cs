using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Increment the view counter of one or more stories.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.incrementStoryViews"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Shares <see cref="IStoryViewRecorder"/> with stories.readStories so both paths agree on when a view
/// counts, and both honour stealth mode.
/// </para>
/// </remarks>
internal sealed class IncrementStoryViewsHandler(
    IStoryAccessService storyAccessService,
    IStoryViewRecorder storyViewRecorder)
    : RpcResultObjectHandler<RequestIncrementStoryViews, IBool>
{
    /// <summary>
    /// Upper bound on the ids accepted in one call. RecordViewsAsync issues several writes per id, so an
    /// unbounded client-supplied list would turn a single RPC into an arbitrary number of round trips.
    /// </summary>
    private const int MaxStoryIds = 100;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestIncrementStoryViews obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var storyIds = obj.Id?.Distinct().Take(MaxStoryIds).ToList() ?? [];
        if (storyIds.Count == 0)
        {
            return new TBoolTrue();
        }

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);

        await storyViewRecorder.RecordViewsAsync(
            peerId,
            peerType,
            storyIds,
            input.UserId,
            context.IsStealthActive(currentTime));

        return new TBoolTrue();
    }
}
