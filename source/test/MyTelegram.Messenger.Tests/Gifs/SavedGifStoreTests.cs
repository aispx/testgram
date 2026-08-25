using System.Reflection;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Gifs;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: the saved-GIF list behind
/// <a href="https://corefork.telegram.org/api/gifs#saved-gifs">messages.saveGif</a>.
///
/// <para>
/// Clients adopt the server's order verbatim and hash it in place, so "newest first" and "a re-save moves
/// to the front" are correctness requirements rather than presentation choices: any other order produces
/// a hash the client never matches. The limit is the server's job too — "the server will automatically
/// delete the oldest GIF" — and clients truncate before hashing, so a list longer than the limit also
/// breaks caching.
/// </para>
/// </summary>
public class SavedGifStoreTests
{
    private const long UserId = 2_000_001;
    private const long OtherUserId = 2_000_002;

    [RequiresMongoDbFact]
    public async Task Newly_saved_gifs_come_first()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10);
        await store.AddAsync(UserId, 333, limit: 10);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([333L, 222L, 111L]);
    }

    [RequiresMongoDbFact]
    public async Task Re_saving_moves_a_gif_back_to_the_front_without_duplicating_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10);
        await store.AddAsync(UserId, 111, limit: 10);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([111L, 222L]);
    }

    [RequiresMongoDbFact]
    public async Task The_oldest_gif_is_dropped_once_the_limit_is_reached()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 2);
        await store.AddAsync(UserId, 222, limit: 2);
        await store.AddAsync(UserId, 333, limit: 2);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([333L, 222L]);
    }

    [RequiresMongoDbFact]
    public async Task The_returned_list_never_exceeds_the_requested_size()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        for (var i = 1; i <= 5; i++)
        {
            await store.AddAsync(UserId, i, limit: 10);
        }

        (await store.GetOrderedIdsAsync(UserId, 3)).ShouldBe([5L, 4L, 3L]);
    }

    [RequiresMongoDbFact]
    public async Task Unsaving_reports_whether_anything_was_there()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);

        (await store.RemoveAsync(UserId, 111)).ShouldBeTrue();
        (await store.RemoveAsync(UserId, 111)).ShouldBeFalse();
        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task One_users_list_is_not_another_users_list()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(OtherUserId, 222, limit: 10);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([111L]);
        (await store.GetOrderedIdsAsync(OtherUserId, 10)).ShouldBe([222L]);
    }

    [RequiresMongoDbFact]
    public async Task Stale_entries_can_be_dropped_in_one_call()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10);
        await store.AddAsync(UserId, 333, limit: 10);

        await store.RemoveManyAsync(UserId, [111L, 333L]);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([222L]);
    }

    [RequiresMongoDbFact]
    public async Task Savers_are_counted_across_users_for_the_local_search_ranking()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(OtherUserId, 111, limit: 10);
        await store.AddAsync(OtherUserId, 222, limit: 10);

        var counts = await store.CountSaversAsync([111L, 222L, 333L]);

        counts[111].ShouldBe(2);
        counts[222].ShouldBe(1);
        counts.ContainsKey(333).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task The_collection_is_indexed_by_user_and_order()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);

        // Index creation is cached in a static field, the way every other store in this codebase does
        // it, so a sibling test that already ran against a different embedded server would otherwise
        // leave this one with no indexes at all.
        typeof(SavedGifStore)
            .GetField("_indexInit", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, null);

        // Reading the list is what creates the indexes; an unindexed collection means a scan per GIF
        // panel open.
        await store.GetOrderedIdsAsync(UserId, 10);

        var indexes = await (await mongo.Database
                .GetCollection<SavedGifDocument>(SavedGifStore.CollectionName)
                .Indexes.ListAsync())
            .ToListAsync();

        indexes.Select(p => p["name"].AsString).ShouldContain("saved_gifs_user_order");
    }
}
