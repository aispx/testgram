using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages.
///
/// <para>
/// The import outlives the request that created it: the parsed messages and the uploaded media wait in
/// MongoDB until the background worker picks the import up, and the lease is what keeps two command
/// servers from importing the same file twice.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class HistoryImportStoreTests
{
    private const long UserId = 2010001;
    private static readonly Peer Channel = new(PeerType.Channel, 1000001);

    [RequiresMongoDbFact]
    public async Task An_import_keeps_its_messages_in_file_order()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);

        var import = await store.CreateAsync(UserId, Channel, ChatExportFormat.WhatsApp, 0, 222, Lines(5));

        import.TotalMessages.ShouldBe(5);
        import.Status.ShouldBe(HistoryImportStatus.Pending);

        var page = await store.ReadMessagesAsync(import.Id, 0, 3);
        page.Select(p => p.Seq).ShouldBe([0, 1, 2]);
        page[0].FromName.ShouldBe("John 0");

        var rest = await store.ReadMessagesAsync(import.Id, 3, 3);
        rest.Select(p => p.Seq).ShouldBe([3, 4]);
    }

    [RequiresMongoDbFact]
    public async Task Two_imports_get_two_ids()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);

        var first = await store.CreateAsync(UserId, Channel, ChatExportFormat.Line, 0, 222, Lines(1));
        var second = await store.CreateAsync(UserId, Channel, ChatExportFormat.Line, 0, 222, Lines(1));

        second.Id.ShouldNotBe(first.Id);
    }

    [RequiresMongoDbFact]
    public async Task An_unfinished_import_of_the_same_chat_is_found()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);

        var import = await store.CreateAsync(UserId, Channel, ChatExportFormat.WhatsApp, 0, 222, Lines(1));

        (await store.GetUnfinishedForPeerAsync(Channel))!.Id.ShouldBe(import.Id);
        (await store.GetUnfinishedForPeerAsync(new Peer(PeerType.User, 2010002))).ShouldBeNull();

        await store.SetStatusAsync(import.Id, HistoryImportStatus.Completed);
        (await store.GetUnfinishedForPeerAsync(Channel)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Uploaded_media_is_returned_by_file_name()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(UserId, Channel, ChatExportFormat.WhatsApp, 1, 222, Lines(1));

        await store.SaveMediaAsync(import.Id, "IMG-0001.jpg", new TMessageMediaContact
        {
            FirstName = "John",
            LastName = "Doe",
            PhoneNumber = "999",
            Vcard = string.Empty
        });

        var media = await store.GetMediaAsync(import.Id, ["IMG-0001.jpg", "missing.jpg"]);

        media.Count.ShouldBe(1);
        media["IMG-0001.jpg"].ShouldBeOfType<TMessageMediaContact>().FirstName.ShouldBe("John");
    }

    [RequiresMongoDbFact]
    public async Task Only_a_queued_import_is_claimed_and_only_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(UserId, Channel, ChatExportFormat.WhatsApp, 0, 222, Lines(1));

        // Still collecting media: the worker must not touch it.
        (await store.ClaimQueuedAsync(60)).ShouldBeNull();

        await store.SetStatusAsync(import.Id, HistoryImportStatus.Queued);

        var claimed = await store.ClaimQueuedAsync(60);
        claimed!.Id.ShouldBe(import.Id);
        claimed.Status.ShouldBe(HistoryImportStatus.Running);

        // A second worker finds nothing while the lease is held.
        (await store.ClaimQueuedAsync(60)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task A_failed_import_is_retried_until_the_attempts_run_out()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(UserId, Channel, ChatExportFormat.WhatsApp, 0, 222, Lines(1));
        await store.SetStatusAsync(import.Id, HistoryImportStatus.Queued);

        await store.FailAsync(import.Id, "boom", maxAttempts: 2);
        var afterFirst = await store.GetAsync(import.Id);
        afterFirst!.Status.ShouldBe(HistoryImportStatus.Queued);
        afterFirst.Attempts.ShouldBe(1);
        afterFirst.LastError.ShouldBe("boom");

        await store.FailAsync(import.Id, "boom", maxAttempts: 2);
        (await store.GetAsync(import.Id))!.Status.ShouldBe(HistoryImportStatus.Failed);
    }

    [RequiresMongoDbFact]
    public async Task A_finished_import_leaves_no_messages_or_media_behind()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(UserId, Channel, ChatExportFormat.WhatsApp, 1, 222, Lines(3));
        await store.SaveMediaAsync(import.Id, "IMG-0001.jpg", new TMessageMediaEmpty());

        await store.CleanupAsync(import.Id);

        (await store.ReadMessagesAsync(import.Id, 0, 10)).ShouldBeEmpty();
        (await store.GetMediaAsync(import.Id, ["IMG-0001.jpg"])).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task The_indexes_can_be_created_twice()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);

        await store.EnsureIndexesAsync();
        await Should.NotThrowAsync(() => store.EnsureIndexesAsync());
    }

    private static HistoryImportStore CreateStore(EmbeddedMongoServer mongo)
    {
        return new HistoryImportStore(mongo.Database, NullLogger<HistoryImportStore>.Instance);
    }

    private static List<ImportedMessageLine> Lines(int count)
    {
        return [.. Enumerable.Range(0, count)
            .Select(i => new ImportedMessageLine(1609459140 + i, $"John {i}", $"message {i}", null))];
    }
}
