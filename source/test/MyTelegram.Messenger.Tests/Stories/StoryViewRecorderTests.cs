using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — view counting.
///
/// <para>
/// A view counts once per (story, viewer). This is the invariant an earlier implementation of
/// stories.readStories broke: it added the <em>size of the batch</em> to every story in the batch, and
/// re-added it on each re-read, so opening ten stories twice left each of them claiming twenty views.
/// </para>
///
/// <para>
/// These run against a real <c>mongod</c> because the guarantee lives in the interaction between the
/// <c>story_views</c> dedup records and the <c>$inc</c> on the story, not in any single expression. They
/// skip cleanly when no MongoDB binary is available.
/// </para>
/// </summary>
public class StoryViewRecorderTests
{
    private const long OwnerId = 100;
    private const long ViewerId = 200;
    private const int PeerType = StoryHelper.PeerTypeUser;

    [RequiresMongoDbFact]
    public async Task Reading_a_batch_adds_exactly_one_view_to_each_story()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1, 2, 3);

        var counted = await recorder.RecordViewsAsync(OwnerId, PeerType, [1, 2, 3], ViewerId, stealthActive: false);

        counted.ShouldBe([1, 2, 3]);
        (await GetViewsAsync(mongo.Database, 1)).ShouldBe(1);
        (await GetViewsAsync(mongo.Database, 2)).ShouldBe(1);
        (await GetViewsAsync(mongo.Database, 3)).ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Re_reading_the_same_stories_does_not_move_the_counters()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1, 2);

        await recorder.RecordViewsAsync(OwnerId, PeerType, [1, 2], ViewerId, stealthActive: false);
        var second = await recorder.RecordViewsAsync(OwnerId, PeerType, [1, 2], ViewerId, stealthActive: false);

        second.ShouldBeEmpty();
        (await GetViewsAsync(mongo.Database, 1)).ShouldBe(1);
        (await GetViewsAsync(mongo.Database, 2)).ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Each_distinct_viewer_counts_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1);

        await recorder.RecordViewsAsync(OwnerId, PeerType, [1], 201, stealthActive: false);
        await recorder.RecordViewsAsync(OwnerId, PeerType, [1], 202, stealthActive: false);
        await recorder.RecordViewsAsync(OwnerId, PeerType, [1], 201, stealthActive: false);

        (await GetViewsAsync(mongo.Database, 1)).ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task The_owner_viewing_their_own_story_is_not_a_view()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1);

        var counted = await recorder.RecordViewsAsync(OwnerId, PeerType, [1], OwnerId, stealthActive: false);

        counted.ShouldBeEmpty();
        (await GetViewsAsync(mongo.Database, 1)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Stealth_mode_leaves_no_trace()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1);

        var counted = await recorder.RecordViewsAsync(OwnerId, PeerType, [1], ViewerId, stealthActive: true);

        counted.ShouldBeEmpty();
        (await GetViewsAsync(mongo.Database, 1)).ShouldBe(0);

        var viewRecords = await mongo.Database
            .GetCollection<BsonDocument>("story_views")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        viewRecords.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task A_view_of_a_story_that_does_not_exist_is_not_recorded()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1);

        // Story 2 was never posted by this peer.
        var counted = await recorder.RecordViewsAsync(OwnerId, PeerType, [1, 2], ViewerId, stealthActive: false);

        counted.ShouldBe([1]);
    }

    [RequiresMongoDbFact]
    public async Task A_deleted_story_does_not_accrue_views()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1);

        await mongo.Database.GetCollection<StoryDocument>("stories").UpdateOneAsync(
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, 1),
            Builders<StoryDocument>.Update.Set(s => s.Deleted, true));

        var counted = await recorder.RecordViewsAsync(OwnerId, PeerType, [1], ViewerId, stealthActive: false);

        counted.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Duplicate_ids_in_one_request_still_count_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var recorder = CreateRecorder(mongo.Database);
        await SeedStoriesAsync(mongo.Database, 1);

        await recorder.RecordViewsAsync(OwnerId, PeerType, [1, 1, 1], ViewerId, stealthActive: false);

        (await GetViewsAsync(mongo.Database, 1)).ShouldBe(1);
    }

    private static IStoryViewRecorder CreateRecorder(IMongoDatabase database)
    {
        return new StoryViewRecorder(database, new MetricsStore(database));
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
                MediaFileId = 1000 + storyId,
                ViewsCount = 0
            });
        }
    }

    private static async Task<int> GetViewsAsync(IMongoDatabase database, int storyId)
    {
        var story = await database.GetCollection<StoryDocument>("stories")
            .Find(Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId))
            .FirstOrDefaultAsync();

        return story.ViewsCount;
    }
}
