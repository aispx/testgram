using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — albums.
///
/// <para>
/// Albums are documents of their own rather than a field on their stories. That is what lets an album
/// survive losing all of its stories, and what lets one story belong to several albums — the shape
/// <c>storyItem.albums: Vector&lt;int&gt;</c> requires and the previous single-<c>AlbumId</c> model could
/// not express. These tests run against a real <c>mongod</c> because the behaviour is the interaction
/// between the album documents, the membership back-reference and the atomic id counter.
/// </para>
/// </summary>
public class StoryAlbumServiceTests
{
    private const long OwnerId = 100;
    private const int PeerType = StoryHelper.PeerTypeUser;

    [RequiresMongoDbFact]
    public async Task Album_ids_are_allocated_from_a_counter_and_increase()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);

        var first = await service.CreateAlbumAsync(OwnerId, PeerType, "First", []);
        var second = await service.CreateAlbumAsync(OwnerId, PeerType, "Second", []);

        first.AlbumId.ShouldBeGreaterThan(0);
        second.AlbumId.ShouldBeGreaterThan(first.AlbumId);
    }

    [RequiresMongoDbFact]
    public async Task Album_ids_are_scoped_per_peer()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);

        var mine = await service.CreateAlbumAsync(OwnerId, PeerType, "Mine", []);
        var theirs = await service.CreateAlbumAsync(999, PeerType, "Theirs", []);

        // Two peers each start their own numbering; neither sees the other's album.
        mine.AlbumId.ShouldBe(theirs.AlbumId);
        (await service.GetAlbumsAsync(OwnerId, PeerType)).Count.ShouldBe(1);
        (await service.GetAlbumsAsync(999, PeerType)).Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task A_story_can_belong_to_several_albums()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);
        await SeedStoriesAsync(mongo.Database, 1);

        var travel = await service.CreateAlbumAsync(OwnerId, PeerType, "Travel", [1]);
        var best = await service.CreateAlbumAsync(OwnerId, PeerType, "Best of", [1]);

        var story = await GetStoryAsync(mongo.Database, 1);
        story.AlbumIds.ShouldBe([travel.AlbumId, best.AlbumId], ignoreOrder: true);
    }

    [RequiresMongoDbFact]
    public async Task Removing_a_story_from_one_album_leaves_its_other_albums_alone()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);
        await SeedStoriesAsync(mongo.Database, 1);

        var travel = await service.CreateAlbumAsync(OwnerId, PeerType, "Travel", [1]);
        var best = await service.CreateAlbumAsync(OwnerId, PeerType, "Best of", [1]);

        await service.RemoveStoriesAsync(OwnerId, PeerType, travel.AlbumId, [1]);

        (await GetStoryAsync(mongo.Database, 1)).AlbumIds.ShouldBe([best.AlbumId]);
    }

    [RequiresMongoDbFact]
    public async Task Deleting_an_album_keeps_its_stories()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);
        await SeedStoriesAsync(mongo.Database, 1, 2);

        var album = await service.CreateAlbumAsync(OwnerId, PeerType, "Trip", [1, 2]);
        await service.DeleteAlbumAsync(OwnerId, PeerType, album.AlbumId);

        (await service.GetAlbumsAsync(OwnerId, PeerType)).ShouldBeEmpty();
        (await GetStoryAsync(mongo.Database, 1)).AlbumIds.ShouldBeEmpty();
        (await GetStoryAsync(mongo.Database, 1)).Deleted.ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task An_emptied_album_still_exists()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);
        await SeedStoriesAsync(mongo.Database, 1);

        var album = await service.CreateAlbumAsync(OwnerId, PeerType, "Trip", [1]);
        await service.RemoveStoriesAsync(OwnerId, PeerType, album.AlbumId, [1]);

        var albums = await service.GetAlbumsAsync(OwnerId, PeerType);
        albums.Count.ShouldBe(1);
        albums[0].IconStoryId.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task The_cover_follows_the_newest_remaining_story()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);
        await SeedStoriesAsync(mongo.Database, 1, 2, 3);

        var album = await service.CreateAlbumAsync(OwnerId, PeerType, "Trip", [1, 2, 3]);
        (await service.GetAlbumAsync(OwnerId, PeerType, album.AlbumId))!.IconStoryId.ShouldBe(3);

        await service.RemoveStoriesAsync(OwnerId, PeerType, album.AlbumId, [3]);
        (await service.GetAlbumAsync(OwnerId, PeerType, album.AlbumId))!.IconStoryId.ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task Reordering_places_the_listed_albums_first_and_keeps_the_rest_after()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);

        var a = await service.CreateAlbumAsync(OwnerId, PeerType, "A", []);
        var b = await service.CreateAlbumAsync(OwnerId, PeerType, "B", []);
        var c = await service.CreateAlbumAsync(OwnerId, PeerType, "C", []);

        // Only B and C are mentioned; A must follow them rather than disappear.
        await service.ReorderAlbumsAsync(OwnerId, PeerType, [c.AlbumId, b.AlbumId]);

        var ordered = await service.GetAlbumsAsync(OwnerId, PeerType);
        ordered.Select(x => x.AlbumId).ShouldBe([c.AlbumId, b.AlbumId, a.AlbumId]);
    }

    [RequiresMongoDbFact]
    public async Task Renaming_an_album_does_not_touch_its_stories()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new StoryAlbumService(mongo.Database, TestFileReferences.Helper);
        await SeedStoriesAsync(mongo.Database, 1);

        var album = await service.CreateAlbumAsync(OwnerId, PeerType, "Old", [1]);
        await service.SetTitleAsync(OwnerId, PeerType, album.AlbumId, "New");

        (await service.GetAlbumAsync(OwnerId, PeerType, album.AlbumId))!.Title.ShouldBe("New");
        (await GetStoryAsync(mongo.Database, 1)).AlbumIds.ShouldBe([album.AlbumId]);
    }

    private static async Task SeedStoriesAsync(IMongoDatabase database, params int[] storyIds)
    {
        var collection = database.GetCollection<StoryDocument>("stories");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var storyId in storyIds)
        {
            await collection.InsertOneAsync(new StoryDocument
            {
                Id = ObjectId.GenerateNewId(),
                OwnerPeerId = OwnerId,
                OwnerPeerType = PeerType,
                StoryId = storyId,
                Date = now,
                ExpireDate = now + 86400,
                MediaType = 1,
                MediaFileId = 1000 + storyId
            });
        }
    }

    private static async Task<StoryDocument> GetStoryAsync(IMongoDatabase database, int storyId)
    {
        return await database.GetCollection<StoryDocument>("stories")
            .Find(Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId))
            .FirstOrDefaultAsync();
    }
}
