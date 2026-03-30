using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class UpdateAlbumHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestUpdateAlbum, IStoryAlbum>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStoryAlbum> HandleCoreAsync(IRequestInput input, RequestUpdateAlbum obj)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        if (obj.AddStories != null && obj.AddStories.Count > 0)
        {
            var filter = Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                Builders<StoryDocument>.Filter.In(s => s.StoryId, obj.AddStories.ToList())
            );
            var update = Builders<StoryDocument>.Update.Set(s => s.AlbumId, obj.AlbumId);
            await _storyCollection.UpdateManyAsync(filter, update);
        }

        if (obj.DeleteStories != null && obj.DeleteStories.Count > 0)
        {
            var filter = Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                Builders<StoryDocument>.Filter.In(s => s.StoryId, obj.DeleteStories.ToList())
            );
            var update = Builders<StoryDocument>.Update.Unset(s => s.AlbumId);
            await _storyCollection.UpdateManyAsync(filter, update);
        }

        var newTitle = obj.Title;
        if (!string.IsNullOrEmpty(newTitle))
        {
            var titleFilter = Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                Builders<StoryDocument>.Filter.Eq(s => s.AlbumId, obj.AlbumId)
            );
            var titleUpdate = Builders<StoryDocument>.Update.Set(s => s.AlbumTitle, newTitle);
            await _storyCollection.UpdateManyAsync(titleFilter, titleUpdate);
        }

        var albumFilter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.Eq(s => s.AlbumId, obj.AlbumId)
        );
        var albumStories = await _storyCollection.Find(albumFilter).ToListAsync();
        var firstStory = albumStories.OrderBy(s => s.StoryId).FirstOrDefault();

        var currentTitle = firstStory?.AlbumTitle ?? $"Album {obj.AlbumId}";

        IPhoto? iconPhoto = null;
        IDocument? iconVideo = null;
        
        if (firstStory != null)
        {
            if (firstStory.MediaType == 1 && firstStory.MediaFileId != 0)
            {
                iconPhoto = new TPhoto
                {
                    Id = firstStory.MediaFileId,
                    AccessHash = firstStory.MediaAccessHash,
                    DcId = firstStory.MediaDcId,
                    FileReference = firstStory.MediaFileReference ?? []
                };
            }
            else if (firstStory.MediaType == 2 && firstStory.MediaFileId != 0)
            {
                iconVideo = new TDocument
                {
                    Id = firstStory.MediaFileId,
                    AccessHash = firstStory.MediaAccessHash,
                    DcId = firstStory.MediaDcId,
                    FileReference = firstStory.MediaFileReference != null ? new ReadOnlyMemory<byte>(firstStory.MediaFileReference) : ReadOnlyMemory<byte>.Empty,
                    Size = firstStory.MediaSize,
                    MimeType = firstStory.MediaMimeType ?? "video/mp4"
                };
            }
        }

        return new TStoryAlbum
        {
            AlbumId = obj.AlbumId,
            Title = currentTitle,
            IconPhoto = iconPhoto,
            IconVideo = iconVideo
        };
    }
}
