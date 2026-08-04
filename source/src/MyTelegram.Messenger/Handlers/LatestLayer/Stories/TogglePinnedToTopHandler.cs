using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Pin some stories to the top of the profile, see <a href="https://corefork.telegram.org/api/stories#pinned-or-archived-stories">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.togglePinnedToTop"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// The request carries the complete desired set, so previously pinned-to-top stories that are absent
/// from <c>id</c> are unpinned.
/// </para>
/// </remarks>
internal sealed class TogglePinnedToTopHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryConfigProvider storyConfigProvider)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestTogglePinnedToTop, IBool>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestTogglePinnedToTop obj)
    {
        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var storyIds = obj.Id?.Distinct().ToList() ?? [];

        var max = storyConfigProvider.GetPinnedToTopMax();
        if (storyIds.Count > max)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        var ownerFilter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        if (storyIds.Count > 0)
        {
            // Every requested story must exist and belong to this peer, otherwise the client's view of
            // the profile would silently diverge from the server's.
            var existingCount = await _storyCollection.CountDocumentsAsync(
                Builders<StoryDocument>.Filter.And(
                    ownerFilter,
                    Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds)));

            if (existingCount != storyIds.Count)
            {
                RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
            }
        }

        // Clear the current selection, then apply the new one.
        await _storyCollection.UpdateManyAsync(
            Builders<StoryDocument>.Filter.And(
                ownerFilter,
                Builders<StoryDocument>.Filter.Eq(s => s.PinnedToTop, true)),
            Builders<StoryDocument>.Update.Set(s => s.PinnedToTop, false));

        if (storyIds.Count > 0)
        {
            await _storyCollection.UpdateManyAsync(
                Builders<StoryDocument>.Filter.And(
                    ownerFilter,
                    Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds)),
                Builders<StoryDocument>.Update
                    .Set(s => s.PinnedToTop, true)
                    // Pinning to the top implies being on the profile at all.
                    .Set(s => s.Pinned, true));
        }

        return new TBoolTrue();
    }
}
