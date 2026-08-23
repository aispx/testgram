using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: storage of Telegram Passport documents.
///
/// <para>
/// <c>account.saveSecureValue</c> stores one value per type per user. Everything but the plain
/// phone/email is end-to-end encrypted, so the server keeps the payload verbatim and only derives the
/// <c>secureValue.hash</c> that <c>account.acceptAuthorization.value_hashes</c> and
/// <c>secureValueError</c> refer to. See https://corefork.telegram.org/passport/encryption
/// </para>
/// </summary>
public class PassportValueStoreTests
{
    private const long UserId = 2010001;
    private const uint PersonalDetails = 0x9d2a81e3;
    private const uint Passport = 0x3dac6a00;
    private const uint Phone = 0xb320aadb;

    [RequiresMongoDbFact]
    public async Task A_saved_value_comes_back_with_its_encrypted_payload_untouched()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        var data = RandomNumberGenerator.GetBytes(64);
        var dataHash = RandomNumberGenerator.GetBytes(32);
        var secret = RandomNumberGenerator.GetBytes(32);

        await store.SaveAsync(UserId, PersonalDetailsValue(data, dataHash, secret));

        var values = await store.ToSecureValuesAsync(UserId, await store.GetAllAsync(UserId));

        var value = values.ShouldHaveSingleItem().ShouldBeOfType<TSecureValue>();
        value.Type.ShouldBeOfType<TSecureValueTypePersonalDetails>();
        var secureData = value.Data.ShouldBeOfType<TSecureData>();
        secureData.Data.ToArray().ShouldBe(data);
        secureData.DataHash.ToArray().ShouldBe(dataHash);
        secureData.Secret.ToArray().ShouldBe(secret);
        // Never null, or the client dereferences them and crashes.
        value.Files.ShouldNotBeNull();
        value.Translation.ShouldNotBeNull();
    }

    [RequiresMongoDbFact]
    public async Task The_value_hash_is_stable_across_reads()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        var saved = await store.SaveAsync(UserId, PersonalDetailsValue());

        var reloaded = (await store.GetAsync(UserId, [PersonalDetails])).ShouldHaveSingleItem();

        saved.Hash.ShouldBe(reloaded.Hash);
        // Clients quote it back in value_hashes, and secureValueError.hash is compared against it.
        saved.Hash.Length.ShouldBe(32);
    }

    [RequiresMongoDbFact]
    public async Task The_hash_of_a_plain_value_is_the_hash_of_the_plaintext()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        var saved = await store.SaveAsync(UserId, new TInputSecureValue
        {
            Type = new TSecureValueTypePhone(),
            PlainData = new TSecurePlainPhone { Phone = "+79001234567" }
        });

        saved.Hash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes("+79001234567")));
    }

    [RequiresMongoDbFact]
    public async Task Two_different_payloads_get_different_hashes()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        var first = await store.SaveAsync(UserId, PersonalDetailsValue());
        var second = await store.SaveAsync(UserId, PersonalDetailsValue());

        first.Hash.ShouldNotBe(second.Hash);
    }

    [RequiresMongoDbFact]
    public async Task Saving_the_same_type_twice_replaces_the_previous_value()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        await store.SaveAsync(UserId, PersonalDetailsValue());
        await store.SaveAsync(UserId, PersonalDetailsValue());

        (await store.GetAllAsync(UserId)).Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task A_file_the_replaced_value_referenced_is_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out var fileStore);

        await SaveUploadAsync(mongo.Database, fileId: 1);
        var first = await store.SaveAsync(UserId, PassportValue(uploadedFileId: 1));
        var orphanedFileId = first.FrontSideFileId!.Value;

        await SaveUploadAsync(mongo.Database, fileId: 2);
        await store.SaveAsync(UserId, PassportValue(uploadedFileId: 2));

        // Otherwise a re-uploaded document leaves its predecessor's scan on the server forever.
        (await fileStore.GetAsync(orphanedFileId, UserId)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Deleting_a_value_deletes_its_files()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out var fileStore);

        await SaveUploadAsync(mongo.Database, fileId: 1);
        var saved = await store.SaveAsync(UserId, PassportValue(uploadedFileId: 1));

        await store.DeleteAsync(UserId, [Passport]);

        (await store.GetAllAsync(UserId)).ShouldBeEmpty();
        (await fileStore.GetAsync(saved.FrontSideFileId!.Value, UserId)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Deleting_one_type_leaves_the_others_alone()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        await store.SaveAsync(UserId, PersonalDetailsValue());
        await store.SaveAsync(UserId, new TInputSecureValue
        {
            Type = new TSecureValueTypePhone(),
            PlainData = new TSecurePlainPhone { Phone = "+79001234567" }
        });

        await store.DeleteAsync(UserId, [Phone]);

        (await store.GetAllAsync(UserId)).ShouldHaveSingleItem().Type.ShouldBe(PersonalDetails);
    }

    [RequiresMongoDbFact]
    public async Task Reusing_an_uploaded_file_that_belongs_to_someone_else_is_FILE_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        await SaveUploadAsync(mongo.Database, fileId: 1);
        var saved = await store.SaveAsync(UserId, PassportValue(uploadedFileId: 1));

        var exception = await Should.ThrowAsync<RpcException>(() => store.SaveAsync(UserId + 1,
            new TInputSecureValue
            {
                Type = new TSecureValueTypePassport(),
                Data = SecureData(),
                FrontSide = new TInputSecureFile { Id = saved.FrontSideFileId!.Value, AccessHash = 1 }
            }));

        exception.RpcError.Message.ShouldBe("FILE_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_value_that_fails_half_way_leaves_no_files_behind()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        await SaveUploadAsync(mongo.Database, fileId: 1);

        // The front side uploads fine; the selfie names parts that were never uploaded.
        await Should.ThrowAsync<RpcException>(() => store.SaveAsync(UserId, new TInputSecureValue
        {
            Type = new TSecureValueTypePassport(),
            Data = SecureData(),
            FrontSide = new TInputSecureFileUploaded
            {
                Id = 1,
                Parts = 1,
                Md5Checksum = string.Empty,
                FileHash = RandomNumberGenerator.GetBytes(32),
                Secret = RandomNumberGenerator.GetBytes(32)
            },
            Selfie = new TInputSecureFileUploaded
            {
                Id = 999,
                Parts = 1,
                Md5Checksum = string.Empty,
                FileHash = RandomNumberGenerator.GetBytes(32),
                Secret = RandomNumberGenerator.GetBytes(32)
            }
        }));

        (await store.GetAllAsync(UserId)).ShouldBeEmpty();
        (await mongo.Database.GetCollection<BsonDocument>("passport_files")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task HasAny_reports_whether_the_user_set_up_passport()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        // account.password.has_secure_values rides on this.
        (await store.HasAnyAsync(UserId)).ShouldBeFalse();

        await store.SaveAsync(UserId, PersonalDetailsValue());

        (await store.HasAnyAsync(UserId)).ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task Values_are_returned_in_the_order_the_types_were_asked_for()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database, out _);

        await store.SaveAsync(UserId, PersonalDetailsValue());
        await store.SaveAsync(UserId, new TInputSecureValue
        {
            Type = new TSecureValueTypePhone(),
            PlainData = new TSecurePlainPhone { Phone = "+79001234567" }
        });

        var documents = await store.GetAsync(UserId, [Phone, PersonalDetails]);

        documents.Select(d => (uint)d.Type).ShouldBe([Phone, PersonalDetails]);
    }

    private static TInputSecureValue PersonalDetailsValue(byte[]? data = null,
        byte[]? dataHash = null,
        byte[]? secret = null)
    {
        return new TInputSecureValue
        {
            Type = new TSecureValueTypePersonalDetails(),
            Data = SecureData(data, dataHash, secret)
        };
    }

    private static TInputSecureValue PassportValue(long uploadedFileId)
    {
        return new TInputSecureValue
        {
            Type = new TSecureValueTypePassport(),
            Data = SecureData(),
            FrontSide = new TInputSecureFileUploaded
            {
                Id = uploadedFileId,
                Parts = 1,
                Md5Checksum = string.Empty,
                FileHash = RandomNumberGenerator.GetBytes(32),
                Secret = RandomNumberGenerator.GetBytes(32)
            }
        };
    }

    private static TSecureData SecureData(byte[]? data = null, byte[]? dataHash = null, byte[]? secret = null)
    {
        return new TSecureData
        {
            Data = data ?? RandomNumberGenerator.GetBytes(64),
            DataHash = dataHash ?? RandomNumberGenerator.GetBytes(32),
            Secret = secret ?? RandomNumberGenerator.GetBytes(32)
        };
    }

    private static IPassportValueStore CreateStore(IMongoDatabase database, out IPassportFileStore fileStore)
    {
        var options = new MyTelegramMessengerServerOptions { ThisDcId = 2 };
        var monitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        monitor.SetupGet(p => p.CurrentValue).Returns(options);

        fileStore = new PassportFileStore(database, monitor.Object);

        return new PassportValueStore(database, fileStore);
    }

    private static async Task SaveUploadAsync(IMongoDatabase database, long fileId)
    {
        await database.GetCollection<BsonDocument>("file_parts").InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"{UserId}_{fileId}_0",
            ["UserId"] = UserId,
            ["FileId"] = fileId,
            ["FilePart"] = 0,
            ["Size"] = 32,
            ["Bytes"] = RandomNumberGenerator.GetBytes(32)
        });
    }
}
