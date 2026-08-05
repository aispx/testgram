using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — which stories belong in the archive.
///
/// <para>
/// Per <a href="https://corefork.telegram.org/api/stories">the API</a>, "after an active story
/// expires, it is automatically added to the story archive" — membership follows from the story's
/// expiry, not from a flag decided when it was posted. <c>SendStoryHandler</c> stamps
/// <c>Archived = true</c> on every story at creation, so a filter keyed on that flag put a story in
/// the archive the instant it was uploaded, while it was still active and should have been showing
/// as a live story instead. These tests pin the boundary to <c>ExpireDate</c>.
/// </para>
/// </summary>
public class StoryArchiveMembershipTests
{
    private const long OwnerId = 2010001;
    private const int PeerType = StoryHelper.PeerTypeUser;

    private static int Now => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static StoryDocument Story(int storyId, int expiresIn, bool archivedFlag = true) => new()
    {
        Id = ObjectId.GenerateNewId(),
        OwnerPeerId = OwnerId,
        OwnerPeerType = PeerType,
        StoryId = storyId,
        Date = Now - 60,
        ExpireDate = Now + expiresIn,
        // Every story carries this from creation, which is exactly why it cannot be the criterion.
        Archived = archivedFlag,
        Deleted = false,
        MediaType = StoryHelper.MediaTypePhoto,
        MediaFileId = 1000 + storyId
    };

    /// <summary>The filter <c>stories.getStoriesArchive</c> applies.</summary>
    private static FilterDefinition<StoryDocument> ArchiveFilter(int now)
    {
        var f = Builders<StoryDocument>.Filter;
        return f.Eq(s => s.OwnerPeerId, OwnerId)
             & f.Eq(s => s.OwnerPeerType, PeerType)
             & f.Lt(s => s.ExpireDate, now)
             & f.Eq(s => s.Deleted, false);
    }

    [RequiresMongoDbFact]
    public async Task A_freshly_posted_story_is_not_in_the_archive()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        // Posted seconds ago with a 24h period — the exact shape of the upload that wrongly
        // appeared in the archive straight away.
        await collection.InsertOneAsync(Story(23001, expiresIn: 86_400));

        var archived = await collection.Find(ArchiveFilter(Now)).ToListAsync();

        archived.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task An_expired_story_is_in_the_archive()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        await collection.InsertOneAsync(Story(11001, expiresIn: -86_400));

        var archived = await collection.Find(ArchiveFilter(Now)).ToListAsync();

        archived.ShouldHaveSingleItem().StoryId.ShouldBe(11001);
    }

    [RequiresMongoDbFact]
    public async Task The_archive_flag_alone_does_not_place_a_story_in_the_archive()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        // Both flagged archived at creation; only expiry decides.
        await collection.InsertManyAsync([
            Story(1, expiresIn: 86_400, archivedFlag: true),
            Story(2, expiresIn: -10, archivedFlag: true)
        ]);

        var archived = await collection.Find(ArchiveFilter(Now)).ToListAsync();

        archived.ShouldHaveSingleItem().StoryId.ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task An_expired_story_missing_the_flag_is_still_in_the_archive()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        // Older rows may predate the flag; expiry is what matters.
        await collection.InsertOneAsync(Story(17001, expiresIn: -3600, archivedFlag: false));

        var archived = await collection.Find(ArchiveFilter(Now)).ToListAsync();

        archived.ShouldHaveSingleItem().StoryId.ShouldBe(17001);
    }

    [RequiresMongoDbFact]
    public async Task A_deleted_story_is_never_in_the_archive()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        var deleted = Story(13001, expiresIn: -86_400);
        deleted.Deleted = true;
        await collection.InsertOneAsync(deleted);

        var archived = await collection.Find(ArchiveFilter(Now)).ToListAsync();

        archived.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_story_expiring_right_now_is_not_yet_archived()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<StoryDocument>("stories");
        var now = Now;
        var story = Story(20001, expiresIn: 0);
        story.ExpireDate = now;
        await collection.InsertOneAsync(story);

        // The boundary is strict: a story is archived once expiry has passed, not at the instant
        // it lands, matching the active-stories filter which admits ExpireDate >= now.
        var archived = await collection.Find(ArchiveFilter(now)).ToListAsync();

        archived.ShouldBeEmpty();
    }
}
