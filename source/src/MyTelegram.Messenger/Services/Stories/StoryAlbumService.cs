using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

public interface IStoryAlbumService
{
    Task<StoryAlbumDocument> CreateAlbumAsync(long ownerPeerId, int ownerPeerType, string title, List<int> storyIds);

    Task<StoryAlbumDocument?> GetAlbumAsync(long ownerPeerId, int ownerPeerType, int albumId);

    Task<List<StoryAlbumDocument>> GetAlbumsAsync(long ownerPeerId, int ownerPeerType);

    Task AddStoriesAsync(long ownerPeerId, int ownerPeerType, int albumId, List<int> storyIds);

    Task RemoveStoriesAsync(long ownerPeerId, int ownerPeerType, int albumId, List<int> storyIds);

    Task SetTitleAsync(long ownerPeerId, int ownerPeerType, int albumId, string title);

    Task SetStoryOrderAsync(long ownerPeerId, int ownerPeerType, int albumId, List<int> order);

    Task DeleteAlbumAsync(long ownerPeerId, int ownerPeerType, int albumId);

    Task ReorderAlbumsAsync(long ownerPeerId, int ownerPeerType, List<int> albumIds);

    /// <summary>Refreshes the album cover to its first remaining story.</summary>
    Task RefreshIconAsync(long ownerPeerId, int ownerPeerType, int albumId);

    /// <summary>Builds the TL album, resolving the cover from the referenced story.</summary>
    Task<IStoryAlbum> ToStoryAlbumAsync(StoryAlbumDocument album);

    Task<List<IStoryAlbum>> ToStoryAlbumListAsync(List<StoryAlbumDocument> albums);
}

/// <summary>
/// Owns the <c>story_albums</c> collection.
/// <para>
/// Albums are first-class documents rather than a field on their stories: an album must survive having
/// all of its stories removed, and a story can belong to several albums
/// (<c>storyItem.albums: Vector&lt;int&gt;</c>). The membership back-reference lives on
/// <see cref="StoryDocument.AlbumIds"/> and is maintained here.
/// </para>
/// </summary>
public class StoryAlbumService(IMongoDatabase mongoDatabase, IFileReferenceHelper fileReferenceHelper) : IStoryAlbumService, ITransientDependency
{
    private readonly IMongoCollection<StoryAlbumDocument> _albumCollection =
        mongoDatabase.GetCollection<StoryAlbumDocument>("story_albums");
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _counterCollection =
        mongoDatabase.GetCollection<BsonDocument>("counters");

    public async Task<StoryAlbumDocument> CreateAlbumAsync(
        long ownerPeerId,
        int ownerPeerType,
        string title,
        List<int> storyIds)
    {
        var albumId = await NextAlbumIdAsync(ownerPeerId, ownerPeerType);
        var existingCount = await _albumCollection.CountDocumentsAsync(OwnerFilter(ownerPeerId, ownerPeerType));

        var album = new StoryAlbumDocument
        {
            Id = StoryAlbumDocument.BuildId(ownerPeerType, ownerPeerId, albumId),
            OwnerPeerId = ownerPeerId,
            OwnerPeerType = ownerPeerType,
            AlbumId = albumId,
            Title = title,
            Order = (int)existingCount,
            Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await _albumCollection.InsertOneAsync(album);

        if (storyIds.Count > 0)
        {
            await AddStoriesAsync(ownerPeerId, ownerPeerType, albumId, storyIds);
            return await GetAlbumAsync(ownerPeerId, ownerPeerType, albumId) ?? album;
        }

        return album;
    }

    public Task<StoryAlbumDocument?> GetAlbumAsync(long ownerPeerId, int ownerPeerType, int albumId)
    {
        return _albumCollection
            .Find(Builders<StoryAlbumDocument>.Filter.And(
                OwnerFilter(ownerPeerId, ownerPeerType),
                Builders<StoryAlbumDocument>.Filter.Eq(a => a.AlbumId, albumId)))
            .FirstOrDefaultAsync()!;
    }

    public Task<List<StoryAlbumDocument>> GetAlbumsAsync(long ownerPeerId, int ownerPeerType)
    {
        return _albumCollection
            .Find(OwnerFilter(ownerPeerId, ownerPeerType))
            .SortBy(a => a.Order)
            .ToListAsync();
    }

    public async Task AddStoriesAsync(long ownerPeerId, int ownerPeerType, int albumId, List<int> storyIds)
    {
        if (storyIds.Count == 0)
        {
            return;
        }

        await _storyCollection.UpdateManyAsync(
            StoryFilter(ownerPeerId, ownerPeerType, storyIds),
            Builders<StoryDocument>.Update.AddToSet(s => s.AlbumIds, albumId));

        await RefreshIconAsync(ownerPeerId, ownerPeerType, albumId);
    }

    public async Task RemoveStoriesAsync(long ownerPeerId, int ownerPeerType, int albumId, List<int> storyIds)
    {
        if (storyIds.Count == 0)
        {
            return;
        }

        // Pull only this album id, so a story stays in the other albums it belongs to.
        await _storyCollection.UpdateManyAsync(
            StoryFilter(ownerPeerId, ownerPeerType, storyIds),
            Builders<StoryDocument>.Update.Pull(s => s.AlbumIds, albumId));

        await _albumCollection.UpdateOneAsync(
            AlbumFilter(ownerPeerId, ownerPeerType, albumId),
            Builders<StoryAlbumDocument>.Update.PullAll(a => a.StoryOrder, storyIds));

        await RefreshIconAsync(ownerPeerId, ownerPeerType, albumId);
    }

    public Task SetTitleAsync(long ownerPeerId, int ownerPeerType, int albumId, string title)
    {
        return _albumCollection.UpdateOneAsync(
            AlbumFilter(ownerPeerId, ownerPeerType, albumId),
            Builders<StoryAlbumDocument>.Update.Set(a => a.Title, title));
    }

    public Task SetStoryOrderAsync(long ownerPeerId, int ownerPeerType, int albumId, List<int> order)
    {
        return _albumCollection.UpdateOneAsync(
            AlbumFilter(ownerPeerId, ownerPeerType, albumId),
            Builders<StoryAlbumDocument>.Update.Set(a => a.StoryOrder, order));
    }

    public async Task DeleteAlbumAsync(long ownerPeerId, int ownerPeerType, int albumId)
    {
        await _albumCollection.DeleteOneAsync(AlbumFilter(ownerPeerId, ownerPeerType, albumId));

        await _storyCollection.UpdateManyAsync(
            Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                Builders<StoryDocument>.Filter.AnyEq(s => s.AlbumIds, albumId)),
            Builders<StoryDocument>.Update.Pull(s => s.AlbumIds, albumId));
    }

    public async Task ReorderAlbumsAsync(long ownerPeerId, int ownerPeerType, List<int> albumIds)
    {
        var albums = await GetAlbumsAsync(ownerPeerId, ownerPeerType);
        var position = 0;

        foreach (var albumId in albumIds)
        {
            if (albums.All(a => a.AlbumId != albumId))
            {
                continue;
            }

            await _albumCollection.UpdateOneAsync(
                AlbumFilter(ownerPeerId, ownerPeerType, albumId),
                Builders<StoryAlbumDocument>.Update.Set(a => a.Order, position));
            position++;
        }

        // Albums the client did not mention keep their relative order after the explicit ones.
        foreach (var album in albums.Where(a => !albumIds.Contains(a.AlbumId)))
        {
            await _albumCollection.UpdateOneAsync(
                AlbumFilter(ownerPeerId, ownerPeerType, album.AlbumId),
                Builders<StoryAlbumDocument>.Update.Set(a => a.Order, position));
            position++;
        }
    }

    public async Task RefreshIconAsync(long ownerPeerId, int ownerPeerType, int albumId)
    {
        var cover = await _storyCollection
            .Find(Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                Builders<StoryDocument>.Filter.AnyEq(s => s.AlbumIds, albumId),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
                Builders<StoryDocument>.Filter.Ne(s => s.MediaFileId, 0)))
            .SortByDescending(s => s.StoryId)
            .Limit(1)
            .FirstOrDefaultAsync();

        await _albumCollection.UpdateOneAsync(
            AlbumFilter(ownerPeerId, ownerPeerType, albumId),
            Builders<StoryAlbumDocument>.Update.Set(a => a.IconStoryId, cover?.StoryId ?? 0));
    }

    public async Task<IStoryAlbum> ToStoryAlbumAsync(StoryAlbumDocument album)
    {
        var list = await ToStoryAlbumListAsync([album]);
        return list[0];
    }

    public async Task<List<IStoryAlbum>> ToStoryAlbumListAsync(List<StoryAlbumDocument> albums)
    {
        var result = new List<IStoryAlbum>();
        if (albums.Count == 0)
        {
            return result;
        }

        // One query for every cover instead of one per album.
        var iconStoryIds = albums.Where(a => a.IconStoryId != 0).Select(a => a.IconStoryId).Distinct().ToList();
        var covers = new Dictionary<(long, int, int), StoryDocument>();

        if (iconStoryIds.Count > 0)
        {
            var owner = albums[0];
            var coverDocs = await _storyCollection
                .Find(StoryFilter(owner.OwnerPeerId, owner.OwnerPeerType, iconStoryIds))
                .ToListAsync();

            foreach (var doc in coverDocs)
            {
                covers[(doc.OwnerPeerId, doc.OwnerPeerType, doc.StoryId)] = doc;
            }
        }

        foreach (var album in albums)
        {
            covers.TryGetValue((album.OwnerPeerId, album.OwnerPeerType, album.IconStoryId), out var cover);

            result.Add(new TStoryAlbum
            {
                AlbumId = album.AlbumId,
                Title = album.Title,
                IconPhoto = StoryHelper.BuildAlbumIconPhoto(fileReferenceHelper, cover),
                IconVideo = StoryHelper.BuildAlbumIconVideo(fileReferenceHelper, cover)
            });
        }

        return result;
    }

    private async Task<int> NextAlbumIdAsync(long ownerPeerId, int ownerPeerType)
    {
        var result = await _counterCollection.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"story_album_id_{ownerPeerType}_{ownerPeerId}"),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        return result["seq"].AsInt32;
    }

    private static FilterDefinition<StoryAlbumDocument> OwnerFilter(long ownerPeerId, int ownerPeerType)
    {
        return Builders<StoryAlbumDocument>.Filter.And(
            Builders<StoryAlbumDocument>.Filter.Eq(a => a.OwnerPeerId, ownerPeerId),
            Builders<StoryAlbumDocument>.Filter.Eq(a => a.OwnerPeerType, ownerPeerType));
    }

    private static FilterDefinition<StoryAlbumDocument> AlbumFilter(long ownerPeerId, int ownerPeerType, int albumId)
    {
        return Builders<StoryAlbumDocument>.Filter.And(
            OwnerFilter(ownerPeerId, ownerPeerType),
            Builders<StoryAlbumDocument>.Filter.Eq(a => a.AlbumId, albumId));
    }

    private static FilterDefinition<StoryDocument> StoryFilter(long ownerPeerId, int ownerPeerType, List<int> storyIds)
    {
        return Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds));
    }
}
