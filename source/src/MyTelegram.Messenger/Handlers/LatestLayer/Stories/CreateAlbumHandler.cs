using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class CreateAlbumHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestCreateAlbum, IStoryAlbum>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStoryAlbum> HandleCoreAsync(IRequestInput input, RequestCreateAlbum obj)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);
        var albumId = ObjectId.GenerateNewId().ToString();
        var albumIdInt = int.Parse(albumId.Substring(0, 8), System.Globalization.NumberStyles.HexNumber);

        IPhoto? iconPhoto = null;
        IDocument? iconVideo = null;

        if (obj.Stories != null && obj.Stories.Count > 0)
        {
            foreach (var storyId in obj.Stories)
            {
                var filter = Builders<StoryDocument>.Filter.And(
                    Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                    Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                    Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId)
                );
                var update = Builders<StoryDocument>.Update.Set(s => s.AlbumId, albumIdInt);
                await _storyCollection.UpdateOneAsync(filter, update);

                if (iconPhoto == null && iconVideo == null)
                {
                    var story = await _storyCollection.Find(filter).FirstOrDefaultAsync();
                    iconPhoto = StoryHelper.BuildAlbumIconPhoto(story);
                    iconVideo = StoryHelper.BuildAlbumIconVideo(story);
                }
            }
        }

        var title = obj.Title ?? string.Empty;
        
        var titleFilter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, obj.Stories ?? [])
        );
        var titleUpdate = Builders<StoryDocument>.Update.Set(s => s.AlbumTitle, title);
        await _storyCollection.UpdateManyAsync(titleFilter, titleUpdate);

        return new TStoryAlbum
        {
            AlbumId = albumIdInt,
            Title = title,
            IconPhoto = iconPhoto,
            IconVideo = iconVideo
        };
    }
}
