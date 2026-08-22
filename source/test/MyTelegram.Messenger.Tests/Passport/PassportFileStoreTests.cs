using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: storage of Telegram Passport files.
///
/// <para>
/// Scans are uploaded with <c>upload.saveFilePart</c> and then referenced from an
/// <c>inputSecureFileUploaded</c>. The bodies are AES-encrypted by the client, so the only integrity
/// check the server can perform is the declared MD5 of the encrypted blob.
/// See https://corefork.telegram.org/passport/encryption#inputsecurefile
/// </para>
/// </summary>
public class PassportFileStoreTests
{
    private const long UserId = 2010001;
    private const long ClientFileId = 4242;

    private static readonly byte[] FileHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Secret = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();

    [RequiresMongoDbFact]
    public async Task An_uploaded_file_becomes_a_secure_file_descriptor()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var blob = await SaveUploadAsync(mongo.Database, [new byte[512], new byte[128]]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 2, Md5(blob), FileHash, Secret);

        document.Id.ShouldNotBe(0);
        document.AccessHash.ShouldNotBe(0);
        document.Size.ShouldBe(640);
        document.Parts.ShouldBe(2);
        document.OwnerUserId.ShouldBe(UserId);
        // file_hash and secret are opaque to the server and must come back exactly as sent, otherwise
        // the receiving service cannot derive the decryption key.
        document.FileHash.ShouldBe(FileHash);
        document.Secret.ShouldBe(Secret);
    }

    [RequiresMongoDbFact]
    public async Task A_missing_part_is_FILE_PARTS_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // Parts 0 and 2: concatenating them would silently produce a blob that decrypts to garbage.
        await SavePartAsync(mongo.Database, 0, new byte[16]);
        await SavePartAsync(mongo.Database, 2, new byte[16]);
        var store = CreateStore(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            store.StoreUploadedAsync(UserId, ClientFileId, 2, null, FileHash, Secret));

        exception.RpcError.Message.ShouldBe("FILE_PARTS_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_part_count_that_does_not_match_is_FILE_PARTS_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[16]]);
        var store = CreateStore(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            store.StoreUploadedAsync(UserId, ClientFileId, 5, null, FileHash, Secret));

        exception.RpcError.Message.ShouldBe("FILE_PARTS_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task No_parts_at_all_is_FILE_EMTPY()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            store.StoreUploadedAsync(UserId, ClientFileId, 0, null, FileHash, Secret));

        exception.RpcError.Message.ShouldBe("FILE_EMTPY");
    }

    [RequiresMongoDbFact]
    public async Task A_wrong_md5_is_MD5_CHECKSUM_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[16]]);
        var store = CreateStore(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            store.StoreUploadedAsync(UserId, ClientFileId, 1, new string('a', 32), FileHash, Secret));

        exception.RpcError.Message.ShouldBe("MD5_CHECKSUM_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_file_over_the_size_cap_is_FILE_PARTS_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[4096]]);
        var store = CreateStore(mongo.Database, maxFileSize: 1024);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            store.StoreUploadedAsync(UserId, ClientFileId, 1, null, FileHash, Secret));

        exception.RpcError.Message.ShouldBe("FILE_PARTS_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_ranged_read_returns_exactly_the_requested_window()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var first = RandomNumberGenerator.GetBytes(512);
        var second = RandomNumberGenerator.GetBytes(512);
        await SaveUploadAsync(mongo.Database, [first, second]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 2, null, FileHash, Secret);

        // A window straddling the part boundary is the case a naive implementation gets wrong.
        var range = await store.LoadRangeAsync(document.Id, 500, 24);

        range.ShouldNotBeNull();
        range!.Value.Bytes.ShouldBe([.. first[500..], .. second[..12]]);
    }

    [RequiresMongoDbFact]
    public async Task A_read_past_the_end_returns_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[64]]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 1, null, FileHash, Secret);

        var range = await store.LoadRangeAsync(document.Id, 1024, 128);

        range.ShouldNotBeNull();
        range!.Value.Bytes.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Reusing_the_client_file_id_does_not_rewrite_the_stored_file()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var original = RandomNumberGenerator.GetBytes(64);
        await SaveUploadAsync(mongo.Database, [original]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 1, null, FileHash, Secret);

        // file_parts is keyed by the CLIENT-chosen id and upserted, so a second upload under the same
        // id must not reach through to the already-stored document.
        await SaveUploadAsync(mongo.Database, [RandomNumberGenerator.GetBytes(64)]);

        var range = await store.LoadRangeAsync(document.Id, 0, 64);

        range!.Value.Bytes.ShouldBe(original);
    }

    [RequiresMongoDbFact]
    public async Task A_file_of_another_user_is_not_resolvable()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[16]]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 1, null, FileHash, Secret);

        (await store.GetAsync(document.Id, UserId)).ShouldNotBeNull();
        (await store.GetAsync(document.Id, UserId + 1)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Deleting_a_file_drops_its_parts()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[16]]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 1, null, FileHash, Secret);

        await store.DeleteAsync([document.Id], UserId);

        (await store.LoadRangeAsync(document.Id, 0, 16)).ShouldBeNull();
        (await mongo.Database.GetCollection<BsonDocument>("passport_file_parts")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task A_delete_requested_by_another_user_is_ignored()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, [new byte[16]]);
        var store = CreateStore(mongo.Database);

        var document = await store.StoreUploadedAsync(UserId, ClientFileId, 1, null, FileHash, Secret);

        await store.DeleteAsync([document.Id], UserId + 1);

        (await store.GetAsync(document.Id, UserId)).ShouldNotBeNull();
    }

    private static IPassportFileStore CreateStore(IMongoDatabase database, long maxFileSize = 10 * 1024 * 1024)
    {
        var options = new MyTelegramMessengerServerOptions { ThisDcId = 2 };
        options.Passport.MaxFileSizeBytes = maxFileSize;

        var monitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        monitor.SetupGet(p => p.CurrentValue).Returns(options);

        return new PassportFileStore(database, monitor.Object);
    }

    private static async Task<byte[]> SaveUploadAsync(IMongoDatabase database, byte[][] parts)
    {
        for (var index = 0; index < parts.Length; index++)
        {
            await SavePartAsync(database, index, parts[index]);
        }

        return parts.SelectMany(p => p).ToArray();
    }

    private static async Task SavePartAsync(IMongoDatabase database, int index, byte[] bytes)
    {
        await database.GetCollection<BsonDocument>("file_parts").ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"{UserId}_{ClientFileId}_{index}"),
            new BsonDocument
            {
                ["_id"] = $"{UserId}_{ClientFileId}_{index}",
                ["UserId"] = UserId,
                ["FileId"] = ClientFileId,
                ["FilePart"] = index,
                ["Size"] = bytes.Length,
                ["Bytes"] = bytes
            },
            new ReplaceOptions { IsUpsert = true });
    }

    private static string Md5(byte[] blob) => Convert.ToHexString(MD5.HashData(blob)).ToLowerInvariant();
}
