using MongoDB.Bson;
using Moq;
using MongoDB.Driver;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Converters.TLObjects.LatestLayer;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Services.TLObjectConverters;

namespace MyTelegram.Messenger.Tests.EmojiStatuses;

/// <summary>
/// Tests for <see cref="EmojiStatusResolver"/> and <see cref="ChannelEmojiStatusValidator"/>: the pieces
/// that decide what an <a href="https://core.telegram.org/api/emoji-status">emoji status</a> actually
/// looks like to a client, and which custom emoji a channel may use as one.
///
/// <para>Both read MongoDB collections directly, so they run against
/// <see cref="EmbeddedMongoServer"/> under <see cref="RequiresMongoDbFactAttribute"/>.</para>
/// </summary>
public class EmojiStatusResolverTests
{
    private const long ModelDocumentId = 5_001;
    private const long PatternDocumentId = 5_002;
    private const long CollectibleId = 777;

    [Fact]
    public void An_absent_status_resolves_to_nothing()
    {
        CreateResolver(null).Resolve(null).ShouldBeNull();
    }

    [Fact]
    public void A_status_without_an_expiry_never_expires()
    {
        var resolver = CreateResolver(null);

        resolver.IsExpired(new EmojiStatus(1)).ShouldBeFalse();
    }

    [Fact]
    public void A_status_whose_until_has_passed_is_expired()
    {
        var resolver = CreateResolver(null);
        var past = (int)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        resolver.IsExpired(new EmojiStatus(1, past)).ShouldBeTrue();
    }

    [Fact]
    public void A_status_expiring_in_the_future_is_still_live()
    {
        var resolver = CreateResolver(null);
        var future = (int)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        resolver.IsExpired(new EmojiStatus(1, future)).ShouldBeFalse();
    }

    [Fact]
    public void An_expired_status_is_not_advertised_to_clients()
    {
        // Previously an expired status kept being served forever, so the emoji stayed next to the
        // name long after the user's chosen expiry.
        var resolver = CreateResolver(null);
        var past = (int)DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();

        resolver.Resolve(new EmojiStatus(42, past)).ShouldBeNull();
    }

    [Fact]
    public void A_plain_status_resolves_without_touching_the_database()
    {
        // A null database would throw if it were used: the common case must stay query-free.
        var status = CreateResolver(null).Resolve(new EmojiStatus(42));

        var emojiStatus = status.ShouldBeOfType<TEmojiStatus>();
        emojiStatus.DocumentId.ShouldBe(42);
    }

    [RequiresMongoDbFact]
    public async Task A_collectible_status_carries_the_gift_decoration()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedGiftAsync(mongo.Database, burned: false, withPattern: true);
        var resolver = CreateResolver(mongo.Database);

        var status = await resolver.ResolveAsync(
            new EmojiStatus(ModelDocumentId, Until: null, CollectibleId: CollectibleId));

        var collectible = status.ShouldBeOfType<TEmojiStatusCollectible>();
        collectible.CollectibleId.ShouldBe(CollectibleId);
        collectible.DocumentId.ShouldBe(ModelDocumentId);
        collectible.Slug.ShouldBe("test-gift");
        collectible.PatternDocumentId.ShouldBe(PatternDocumentId);
        collectible.CenterColor.ShouldBe(0x112233);
    }

    [RequiresMongoDbFact]
    public async Task A_pattern_that_was_never_uploaded_is_not_advertised()
    {
        // Referencing a missing document makes clients render a broken emoji.
        using var mongo = EmbeddedMongoServer.Start();
        await SeedGiftAsync(mongo.Database, burned: false, withPattern: false);
        var resolver = CreateResolver(mongo.Database);

        var status = await resolver.ResolveAsync(
            new EmojiStatus(ModelDocumentId, Until: null, CollectibleId: CollectibleId));

        status.ShouldBeOfType<TEmojiStatusCollectible>().PatternDocumentId.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task A_burned_collectible_degrades_to_a_plain_status()
    {
        // The gift is gone, but the emoji the user picked is still shown — just without decoration.
        using var mongo = EmbeddedMongoServer.Start();
        await SeedGiftAsync(mongo.Database, burned: true, withPattern: true);
        var resolver = CreateResolver(mongo.Database);

        var status = await resolver.ResolveAsync(
            new EmojiStatus(ModelDocumentId, Until: null, CollectibleId: CollectibleId));

        status.ShouldBeOfType<TEmojiStatus>().DocumentId.ShouldBe(ModelDocumentId);
    }

    [RequiresMongoDbFact]
    public async Task Resolving_many_statuses_keeps_collectibles_and_drops_expired_ones()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedGiftAsync(mongo.Database, burned: false, withPattern: true);
        var resolver = CreateResolver(mongo.Database);
        var past = (int)DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();

        var resolved = await resolver.ResolveManyAsync(
        [
            new(1, new EmojiStatus(ModelDocumentId, Until: null, CollectibleId: CollectibleId)),
            new(2, new EmojiStatus(9_999)),
            new(3, new EmojiStatus(8_888, past))
        ]);

        resolved.Count.ShouldBe(2);
        resolved[1].ShouldBeOfType<TEmojiStatusCollectible>();
        resolved[2].ShouldBeOfType<TEmojiStatus>();
        resolved.ShouldNotContainKey(3);
    }

    private static EmojiStatusResolver CreateResolver(IMongoDatabase? database)
    {
        // The resolver grabs its collections up front, so even the query-free cases need a database
        // object; a loose mock stands in when the test must not reach MongoDB at all.
        return new EmojiStatusResolver(
            new FakeEmojiStatusConverterService(),
            database ?? new Mock<IMongoDatabase>(MockBehavior.Loose).Object);
    }

    private static async Task SeedGiftAsync(IMongoDatabase database, bool burned, bool withPattern)
    {
        await database.GetCollection<UniqueStarGiftDocument>("unique-star-gifts").InsertOneAsync(
            new UniqueStarGiftDocument
            {
                UniqueId = CollectibleId,
                Title = "Test Gift",
                Slug = "test-gift",
                Num = 1,
                Burned = burned,
                DocumentId = ModelDocumentId,
                Attributes =
                [
                    new UniqueGiftAttribute { Type = "model", DocumentId = ModelDocumentId },
                    new UniqueGiftAttribute { Type = "pattern", DocumentId = PatternDocumentId },
                    new UniqueGiftAttribute
                    {
                        Type = "backdrop",
                        CenterColor = 0x112233,
                        EdgeColor = 0x445566,
                        PatternColor = 0x778899,
                        TextColor = 0xAABBCC
                    }
                ]
            });

        var documents = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        await documents.InsertOneAsync(new BsonDocument { ["DocumentId"] = ModelDocumentId });
        if (withPattern)
        {
            await documents.InsertOneAsync(new BsonDocument { ["DocumentId"] = PatternDocumentId });
        }
    }

    /// <summary>
    /// The layered converter registry resolved through DI in production; only the latest layer is
    /// exercised here.
    /// </summary>
    private sealed class FakeEmojiStatusConverterService : ILayeredService<IEmojiStatusConverter>
    {
        private readonly EmojiStatusConverter _converter = new();

        public IEmojiStatusConverter Converter => _converter;

        public IEmojiStatusConverter GetConverter(int layer) => _converter;
    }
}

/// <summary>
/// Which custom emoji a channel may use as its status, and the restricted list served by
/// <c>account.getChannelRestrictedStatusEmojis</c>.
/// </summary>
public class ChannelEmojiStatusValidatorTests
{
    [RequiresMongoDbFact]
    public async Task Without_a_channel_status_pack_any_emoji_is_allowed()
    {
        // Otherwise a server that ships no curated pack could never set a channel status at all.
        using var mongo = EmbeddedMongoServer.Start();
        var validator = new ChannelEmojiStatusValidator(mongo.Database);

        (await validator.IsAllowedAsync(123)).ShouldBeTrue();
        (await validator.GetAllowedDocumentIdsAsync()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Only_emoji_from_a_channel_status_pack_are_allowed()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedSetAsync(mongo.Database, channelEmojiStatus: true, documentIds: [10, 11]);
        await SeedSetAsync(mongo.Database, channelEmojiStatus: false, documentIds: [20]);
        var validator = new ChannelEmojiStatusValidator(mongo.Database);

        (await validator.IsAllowedAsync(10)).ShouldBeTrue();
        (await validator.IsAllowedAsync(20)).ShouldBeFalse();
        (await validator.GetAllowedDocumentIdsAsync()).ShouldBe([10, 11]);
    }

    [RequiresMongoDbFact]
    public async Task Restricted_emoji_are_rejected_and_excluded_from_the_allowed_list()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedSetAsync(mongo.Database, channelEmojiStatus: true, documentIds: [10, 11]);
        await mongo.Database.GetCollection<BsonDocument>("channel_restricted_status_emojis")
            .InsertOneAsync(new BsonDocument { ["DocumentId"] = 11L });
        var validator = new ChannelEmojiStatusValidator(mongo.Database);

        (await validator.IsAllowedAsync(11)).ShouldBeFalse();
        (await validator.GetAllowedDocumentIdsAsync()).ShouldBe([10]);
        (await validator.GetRestrictedDocumentIdsAsync()).ShouldBe([11]);
    }

    private static async Task SeedSetAsync(IMongoDatabase database, bool channelEmojiStatus, long[] documentIds)
    {
        await database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel").InsertOneAsync(
            new BsonDocument
            {
                ["StickerSetId"] = channelEmojiStatus ? 1L : 2L,
                ["ChannelEmojiStatus"] = channelEmojiStatus,
                ["DocumentIds"] = new BsonArray(documentIds)
            });
    }
}
