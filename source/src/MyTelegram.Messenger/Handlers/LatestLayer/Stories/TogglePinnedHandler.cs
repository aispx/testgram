using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Pin or unpin one or more stories on a peer's profile, see
/// <a href="https://corefork.telegram.org/api/stories#pinned-or-archived-stories">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.togglePinned"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class TogglePinnedHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestTogglePinned, TVector<int>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<TVector<int>> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestTogglePinned obj)
    {
        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var storyIds = obj.Id?.Distinct().ToList() ?? [];
        if (storyIds.Count == 0)
        {
            return new TVector<int>();
        }

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds)
        );

        var affected = await _storyCollection.Find(filter).ToListAsync();
        if (affected.Count == 0)
        {
            return new TVector<int>();
        }

        var update = obj.Pinned
            ? Builders<StoryDocument>.Update.Set(s => s.Pinned, true)
            // Unpinning from the profile also drops the story from the pinned-to-top row.
            : Builders<StoryDocument>.Update
                .Set(s => s.Pinned, false)
                .Set(s => s.PinnedToTop, false);

        await _storyCollection.UpdateManyAsync(filter, update);

        return new TVector<int>(affected.Select(s => s.StoryId).ToList());
    }
}
