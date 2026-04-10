using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class GetAlbumsHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetAlbums, IAlbums>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IAlbums> HandleCoreAsync(IRequestInput input, RequestGetAlbums obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Ne(s => s.AlbumId, null),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();

        var albumsDict = new Dictionary<int, List<StoryDocument>>();
        foreach (var story in stories)
        {
            if (story.AlbumId.HasValue)
            {
                if (!albumsDict.ContainsKey(story.AlbumId.Value))
                {
                    albumsDict[story.AlbumId.Value] = new List<StoryDocument>();
                }
                albumsDict[story.AlbumId.Value].Add(story);
            }
        }

        var albumsList = new TVector<IStoryAlbum>();
        foreach (var album in albumsDict)
        {
            var firstStory = album.Value.OrderBy(s => s.StoryId).FirstOrDefault();

            // Skip albums without valid media
            if (firstStory == null || firstStory.MediaFileId == 0)
            {
                continue;
            }

            IPhoto? iconPhoto = null;
            IDocument? iconVideo = null;

            if (firstStory.MediaType == 1)
            {
                iconPhoto = new TPhoto
                {
                    Id = firstStory.MediaFileId,
                    AccessHash = firstStory.MediaAccessHash,
                    DcId = firstStory.MediaDcId,
                    FileReference = firstStory.MediaFileReference ?? [],
                    Date = (int)firstStory.Date,
                    Sizes = new TVector<IPhotoSize>()
                };
            }
            else if (firstStory.MediaType == 2)
            {
                iconVideo = new TDocument
                {
                    Id = firstStory.MediaFileId,
                    AccessHash = firstStory.MediaAccessHash,
                    DcId = firstStory.MediaDcId,
                    FileReference = firstStory.MediaFileReference != null ? new ReadOnlyMemory<byte>(firstStory.MediaFileReference) : ReadOnlyMemory<byte>.Empty,
                    Size = firstStory.MediaSize,
                    MimeType = firstStory.MediaMimeType ?? "video/mp4",
                    Date = (int)firstStory.Date,
                    Thumbs = new TVector<IPhotoSize>(),
                    VideoThumbs = new TVector<IVideoSize>(),
                    Attributes = new TVector<IDocumentAttribute>()
                };
            }
            else
            {
                // Unknown media type - skip this album
                continue;
            }
            
            albumsList.Add(new TStoryAlbum
            {
                AlbumId = album.Key,
                Title = firstStory?.AlbumTitle ?? $"Album {album.Key}",
                IconPhoto = iconPhoto,
                IconVideo = iconVideo
            });
        }

        return new TAlbums
        {
            Albums = albumsList,
            Hash = 0
        };
    }
}
