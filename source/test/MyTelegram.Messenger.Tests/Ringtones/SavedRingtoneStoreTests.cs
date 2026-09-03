using MyTelegram.Messenger.Services.Ringtones;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Ringtones;

/// <summary>
/// Feature: the saved notification sound list behind
/// <a href="https://corefork.telegram.org/api/ringtones">account.getSavedRingtones</a>.
///
/// <para>
/// Clients render the vector as received and a freshly uploaded sound belongs at the front (iOS prepends
/// it locally, tdesktop inserts it at <c>begin</c>), so "newest first" is a wire contract. Unlike saved
/// GIFs, re-saving must <b>not</b> reorder: tdlib calls <c>account.saveRingtone</c> after every upload, and
/// a list that reshuffles on that would change a hash the client has already stored.
/// </para>
/// </summary>
public class SavedRingtoneStoreTests
{
    private const long UserId = 2_000_001;
    private const long OtherUserId = 2_000_002;

    [RequiresMongoDbFact]
    public async Task Newly_saved_sounds_come_first()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10);
        await store.AddAsync(UserId, 333, limit: 10);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([333L, 222L, 111L]);
    }

    [RequiresMongoDbFact]
    public async Task Saving_the_same_sound_again_neither_duplicates_nor_reorders_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10);

        var added = await store.AddAsync(UserId, 111, limit: 10);

        added.ShouldBeFalse();
        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([222L, 111L]);
    }

    [RequiresMongoDbFact]
    public async Task The_oldest_sound_is_dropped_once_the_limit_is_reached()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 2);
        await store.AddAsync(UserId, 222, limit: 2);
        await store.AddAsync(UserId, 333, limit: 2);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([333L, 222L]);
    }

    [RequiresMongoDbFact]
    public async Task One_users_sounds_are_invisible_to_another()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(OtherUserId, 222, limit: 10);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([111L]);
        (await store.GetOrderedIdsAsync(OtherUserId, 10)).ShouldBe([222L]);
    }

    /// <summary>
    /// A converted sound is stored under the MP3 twin's id, and a client that still refers to the document it
    /// passed in has to be able to find — and therefore unsave — the entry.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_converted_sound_is_found_by_the_id_the_client_saved()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, documentId: 999, limit: 10, originalDocumentId: 111);

        var byServed = await store.FindAsync(UserId, 999);
        var byOriginal = await store.FindAsync(UserId, 111);

        byServed!.DocumentId.ShouldBe(999L);
        byOriginal!.DocumentId.ShouldBe(999L);
        byOriginal.OriginalDocumentId.ShouldBe(111L);
    }

    [RequiresMongoDbFact]
    public async Task An_unsaved_sound_leaves_the_list_and_removing_a_missing_one_reports_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);

        (await store.RemoveAsync(UserId, 111)).ShouldBeTrue();
        (await store.RemoveAsync(UserId, 111)).ShouldBeFalse();
        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBeEmpty();
    }

    /// <summary>
    /// <c>account.getSavedRingtones</c> drops a sound whose document is gone from the list and from the
    /// collection at once, so what is stored can never disagree with what is served.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Stale_sounds_are_removed_in_one_call()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10);
        await store.AddAsync(UserId, 333, limit: 10);

        await store.RemoveManyAsync(UserId, [111L, 333L]);

        (await store.GetOrderedIdsAsync(UserId, 10)).ShouldBe([222L]);
    }

    /// <summary>
    /// The duration is probed when the sound is uploaded and kept here, because the document row belongs to
    /// the file server and does not carry one. A later save that finally knows it fills it in without moving
    /// the entry.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_probed_duration_is_stored_with_the_sound()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedRingtoneStore(mongo.Database);

        await store.AddAsync(UserId, 111, limit: 10, info: new RingtoneAudioInfo(3, "Chime", "Someone"));
        await store.AddAsync(UserId, 222, limit: 10);
        await store.AddAsync(UserId, 222, limit: 10, info: new RingtoneAudioInfo(4, null, null));

        var rows = await store.GetOrderedAsync(UserId, 10);

        rows.ConvertAll(p => p.DocumentId).ShouldBe([222L, 111L]);
        rows[1].DurationSeconds.ShouldBe(3);
        rows[1].Title.ShouldBe("Chime");
        rows[1].Performer.ShouldBe("Someone");
        rows[0].DurationSeconds.ShouldBe(4);
    }
}
