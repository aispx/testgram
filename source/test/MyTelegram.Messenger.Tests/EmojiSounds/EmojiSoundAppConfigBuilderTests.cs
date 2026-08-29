using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Emoji;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.EmojiSounds;

/// <summary>
/// Tests for the <c>emojies_sounds</c> entry of <c>help.getAppConfig</c> — the soundbites clients play
/// when an animated emoji is clicked (https://corefork.telegram.org/api/animated-emojis#emojis-with-sounds).
///
/// <para>Two things about it are not free choices. Every field has to be a <c>jsonString</c>, because
/// tdlib's <c>ConfigManager</c> ignores members of a sound object that are not strings and then drops
/// the entry for a missing id, and Android casts to <c>TL_jsonString</c> the same way — a numeric id is
/// silently discarded by every client. And the hash has to move with the per-session access hashes in
/// the entry, or a client that re-logs in is answered <c>appConfigNotModified</c> while holding hashes
/// minted for its previous authorization, which download nothing.</para>
/// </summary>
public class EmojiSoundAppConfigBuilderTests
{
    private static readonly byte[] Reference = [0xFF, 0xEF, 0xBE, 0x01, 0x02, 0x03];

    private static IEmojiSoundAppConfigBuilder Builder(IReadOnlyList<EmojiSound> sounds,
        Func<long, long, long, long>? accessHash = null)
    {
        var store = new Mock<IEmojiSoundStore>();
        store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(sounds);

        var helper = new Mock<IAccessHashHelper2>();
        helper.Setup(p => p.GenerateAccessHash(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<AccessHashType>()))
            .Returns((long userId, long keyId, long targetId, AccessHashType _) =>
                accessHash?.Invoke(userId, keyId, targetId) ?? targetId + keyId);

        return new EmojiSoundAppConfigBuilder(store.Object, helper.Object, TestFileReferences.Helper);
    }

    private static IRequestWithAccessHashKeyId Request(long userId = 2010001, long accessHashKeyId = 777)
    {
        var request = new Mock<IRequestWithAccessHashKeyId>();
        request.SetupGet(p => p.UserId).Returns(userId);
        request.SetupGet(p => p.AccessHashKeyId).Returns(accessHashKeyId);

        return request.Object;
    }

    [Fact]
    public async Task Nothing_seeded_serves_no_key_at_all()
    {
        // Telegram omits keys it has no value for; an empty jsonObject would make every client throw
        // its cached soundbites away and gain nothing.
        var entry = await Builder([]).BuildAsync(Request());

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task Every_field_is_a_json_string()
    {
        var entry = await Builder([new EmojiSound("🎃", 4956223179606458539, Reference)])
            .BuildAsync(Request(accessHashKeyId: 5));

        entry.ShouldNotBeNull();
        entry.Value.Key.ShouldBe("emojies_sounds");

        var map = entry.Value.Value.ShouldBeOfType<TJsonObject>();
        var sound = map.Value.ShouldHaveSingleItem().ShouldBeOfType<TJsonObjectValue>();
        sound.Key.ShouldBe("🎃");

        var fields = sound.Value.ShouldBeOfType<TJsonObject>().Value
            .Cast<TJsonObjectValue>()
            .ToDictionary(p => p.Key, p => p.Value);

        fields.Keys.ShouldBe(["id", "access_hash", "file_reference_base64"], ignoreOrder: true);
        foreach (var value in fields.Values)
        {
            value.ShouldBeOfType<TJsonString>();
        }

        ((TJsonString)fields["id"]).Value.ShouldBe("4956223179606458539");
        ((TJsonString)fields["access_hash"]).Value.ShouldBe("4956223179606458544");
    }

    [Fact]
    public async Task File_reference_is_unpadded_base64url()
    {
        var entry = await Builder([new EmojiSound("🎃", 1, Reference)]).BuildAsync(Request());

        var reference = ReferenceOf(entry, "🎃");

        // The URL-safe alphabet and no padding are what is_base64url accepts and what
        // Base64.URL_SAFE decodes.
        reference.ShouldNotContain("=");
        reference.ShouldNotContain("+");
        reference.ShouldNotContain("/");
    }

    /// <summary>
    /// The reference served here is minted from the document id, not copied out of the row, because it is
    /// a real reference that clients quote back in <c>upload.getFile</c> and the stored value expires.
    /// See https://corefork.telegram.org/api/file-references
    /// </summary>
    [Fact]
    public async Task File_reference_is_minted_from_the_document_id()
    {
        var entry = await Builder([new EmojiSound("🎃", 4242, Reference)]).BuildAsync(Request());

        var expected = EmojiSoundAppConfigBuilder.ToBase64Url(
            TestFileReferences.Helper.Create(AccessHashType.Document, 4242));

        ReferenceOf(entry, "🎃").ShouldBe(expected);
    }

    /// <summary>
    /// A soundbite whose row carries no reference — every row, after the migration — must still be served
    /// with one. An empty <c>file_reference_base64</c> is a reference no client can download with once
    /// checking is enforced.
    /// </summary>
    [Fact]
    public async Task An_empty_stored_reference_is_not_served_empty()
    {
        var entry = await Builder([new EmojiSound("🎃", 1, [])]).BuildAsync(Request());

        ReferenceOf(entry, "🎃").ShouldNotBeEmpty();
    }

    private static string ReferenceOf(EmojiSoundAppConfigEntry? entry, string emoticon)
    {
        return entry!.Value.Value.ShouldBeOfType<TJsonObject>().Value
            .Cast<TJsonObjectValue>()
            .Single(p => p.Key == emoticon).Value
            .ShouldBeOfType<TJsonObject>().Value
            .Cast<TJsonObjectValue>()
            .Single(p => p.Key == "file_reference_base64").Value
            .ShouldBeOfType<TJsonString>().Value;
    }

    [Fact]
    public async Task Same_session_hashes_identically()
    {
        var sounds = new[] { new EmojiSound("🎃", 10, Reference), new EmojiSound("🦾", 11, Reference) };

        var first = await Builder(sounds).BuildAsync(Request());
        var second = await Builder(sounds).BuildAsync(Request());

        first!.Hash.ShouldBe(second!.Hash);
        first.Hash.ShouldNotBe(0);
    }

    [Fact]
    public async Task A_different_authorization_hashes_differently()
    {
        var sounds = new[] { new EmojiSound("🎃", 10, Reference) };

        var first = await Builder(sounds).BuildAsync(Request(accessHashKeyId: 1));
        var second = await Builder(sounds).BuildAsync(Request(accessHashKeyId: 2));

        first!.Hash.ShouldNotBe(second!.Hash);
    }

    [Fact]
    public async Task Different_sounds_hash_differently()
    {
        var first = await Builder([new EmojiSound("🎃", 10, Reference)]).BuildAsync(Request());
        var second = await Builder([new EmojiSound("🦾", 10, Reference)]).BuildAsync(Request());
        var third = await Builder([new EmojiSound("🎃", 11, Reference)]).BuildAsync(Request());

        new[] { first!.Hash, second!.Hash, third!.Hash }.Distinct().Count().ShouldBe(3);
    }
}

/// <summary>
/// Tests for <see cref="EmojiSoundStore"/>, which reads the seeded <c>emoji_sounds</c> collection and
/// resolves each entry against the document read model. The BSON shapes it has to tolerate — a file
/// reference written as Binary by the server and as an array of numbers by the seeder — are the
/// substance, so these run against a real <c>mongod</c>.
/// </summary>
public class EmojiSoundStoreTests
{
    [RequiresMongoDbFact]
    public async Task Sounds_are_served_in_seeded_order_with_their_file_reference()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await InsertSoundAsync(mongo.Database, "🦾", 11, order: 1);
        await InsertSoundAsync(mongo.Database, "🎃", 10, order: 0);
        await InsertDocumentAsync(mongo.Database, 10, new BsonArray(new[] { 1, 2, 3 }));
        await InsertDocumentAsync(mongo.Database, 11, new BsonBinaryData([4, 5, 6]));

        var sounds = await Store(mongo).GetAllAsync();

        sounds.Select(p => p.Emoticon).ShouldBe(["🎃", "🦾"]);
        sounds[0].FileReference.ShouldBe([1, 2, 3]);
        sounds[1].FileReference.ShouldBe([4, 5, 6]);
    }

    [RequiresMongoDbFact]
    public async Task A_sound_whose_document_is_missing_is_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();

        await InsertSoundAsync(mongo.Database, "🎃", 10, order: 0);
        await InsertSoundAsync(mongo.Database, "🦾", 11, order: 1);
        await InsertDocumentAsync(mongo.Database, 10, new BsonBinaryData([1]));

        // Serving an id that cannot be downloaded makes the client retry it on every refresh, which is
        // exactly how the blank emoji pickers behaved before account.getDefault*Emojis was fixed.
        var sounds = await Store(mongo).GetAllAsync();

        sounds.ShouldHaveSingleItem().Emoticon.ShouldBe("🎃");
    }

    [RequiresMongoDbFact]
    public async Task An_empty_collection_serves_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();

        (await Store(mongo).GetAllAsync()).ShouldBeEmpty();
    }

    private static EmojiSoundStore Store(EmbeddedMongoServer mongo)
    {
        return new EmojiSoundStore(mongo.Database, new Mock<ILogger<EmojiSoundStore>>().Object);
    }

    private static Task InsertSoundAsync(IMongoDatabase database, string emoticon, long documentId, int order)
    {
        return database.GetCollection<BsonDocument>(EmojiSoundStore.CollectionName).InsertOneAsync(
            new BsonDocument
            {
                { "_id", emoticon },
                { "Emoticon", emoticon },
                { "DocumentId", documentId },
                { "Order", order }
            });
    }

    private static Task InsertDocumentAsync(IMongoDatabase database, long documentId, BsonValue fileReference)
    {
        return database.GetCollection<BsonDocument>(EmojiSoundStore.DocumentCollectionName).InsertOneAsync(
            new BsonDocument
            {
                { "_id", $"documentreadmodel-{documentId}" },
                { "DocumentId", documentId },
                { "MimeType", "audio/ogg" },
                { "FileReference", fileReference }
            });
    }
}
