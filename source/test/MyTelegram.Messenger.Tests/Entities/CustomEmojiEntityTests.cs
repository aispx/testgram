using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Entities;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Entities;

/// <summary>
/// Feature: <c>messageEntityCustomEmoji</c> on the way in — see
/// <a href="https://corefork.telegram.org/api/custom-emoji">custom emojis</a>.
///
/// <para>
/// Two rules from that page are the server's to apply. An entity "must wrap exactly one regular emoji
/// (the one contained in <c>documentAttributeCustomEmoji.alt</c>) in the related text, otherwise the
/// server will <b>ignore</b> it" — ignore, not refuse: a forward carrying a document id this server no
/// longer knows, or a client whose sticker cache was cleared, has to be able to send its text. And "you
/// can attach a maximum of <c>message_animated_emoji_max</c> custom emojis, as specified by the
/// appConfig field", which no client checks — neither tdlib nor Android reads the field — so the server
/// is the only place that limit can hold.
/// </para>
/// </summary>
public class CustomEmojiEntityTests
{
    private const long KnownDocumentId = 5_357_348_626_559_411_204;
    private const long SiblingDocumentId = 5_355_088_357_070_218_139;
    private const long PlainDocumentId = 5_357_465_415_310_124_060;
    private const long UnknownDocumentId = 1;
    private const long StickerSetId = 773_947_703_670_341_676;
    private const string Grin = "😀";
    private const string Heart = "❤";

    private static async Task SeedAsync(IMongoDatabase database, string stickerSetIdField = "Id")
    {
        var stickerset = new BsonDocument
        {
            ["_t"] = nameof(TInputStickerSetID),
            [stickerSetIdField] = StickerSetId,
            ["AccessHash"] = 1234L
        };

        await database.GetCollection<BsonDocument>("eventflow-documentreadmodel").InsertManyAsync(
        [
            CustomEmojiDocument(KnownDocumentId, Grin, stickerset),
            CustomEmojiDocument(SiblingDocumentId, Heart, stickerset),
            new BsonDocument
            {
                ["DocumentId"] = PlainDocumentId,
                // Neither a custom-emoji nor a sticker attribute: the one shape that cannot stand in
                // for a custom emoji.
                ["Attributes2"] = new BsonArray
                {
                    new BsonDocument { ["_t"] = nameof(TDocumentAttributeFilename), ["FileName"] = "a.webp" }
                }
            }
        ]);

        await database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel").InsertOneAsync(
            new BsonDocument
            {
                ["StickerSetId"] = StickerSetId,
                ["ShortName"] = "StatusPack",
                ["Emojis"] = true,
                ["DocumentIds"] = new BsonArray { KnownDocumentId, SiblingDocumentId },
                ["Packs"] = new BsonArray
                {
                    Pack(Grin, KnownDocumentId),
                    Pack(Heart, SiblingDocumentId)
                }
            });
    }

    private static BsonDocument CustomEmojiDocument(long documentId, string alt, BsonDocument stickerset)
    {
        return new BsonDocument
        {
            ["DocumentId"] = documentId,
            ["Attributes2"] = new BsonArray
            {
                new BsonDocument
                {
                    ["_t"] = nameof(TDocumentAttributeCustomEmoji),
                    ["Alt"] = alt,
                    ["Free"] = true,
                    ["Stickerset"] = stickerset
                }
            }
        };
    }

    private static BsonDocument Pack(string emoticon, long documentId)
    {
        return new BsonDocument
        {
            ["Emoticon"] = emoticon,
            ["Documents"] = new BsonArray { documentId }
        };
    }

    [RequiresMongoDbFact]
    public async Task An_entity_wrapping_its_own_alt_is_kept()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync(Grin, [CustomEmoji(0, Grin.Length, KnownDocumentId)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        var entities = result.Entities.ShouldNotBeNull();
        entities.Count.ShouldBe(1);
        entities[0].ShouldBeOfType<TMessageEntityCustomEmoji>().DocumentId.ShouldBe(KnownDocumentId);
    }

    /// <summary>
    /// The id the client sent resolves to nothing here. Refusing the send would mean a text forwarded
    /// from anywhere else can never be re-sent.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_entity_naming_an_unknown_document_is_dropped_and_the_text_still_goes_out()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync(Grin, [CustomEmoji(0, Grin.Length, UnknownDocumentId)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        result.Entities.ShouldBeNull();
    }

    /// <summary>
    /// <c>document_id = 0</c> used to be answered with <c>DOCUMENT_INVALID</c> from the validator, which
    /// is the one shape of this that failed before the database was even consulted.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_entity_with_a_zero_document_id_is_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync(Grin, [CustomEmoji(0, Grin.Length, 0)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        result.Entities.ShouldBeNull();
    }

    /// <summary>
    /// A document that exists but is a plain sticker, or carries no attributes at all, is not a custom
    /// emoji: same outcome, and this is also how an entity pointing into the legacy AnimatedEmojies set
    /// arrives.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_entity_naming_a_document_that_is_not_a_custom_emoji_is_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync(Grin, [CustomEmoji(0, Grin.Length, PlainDocumentId)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        result.Entities.ShouldBeNull();
    }

    /// <summary>
    /// The text under the entity is a different emoji than the document's <c>alt</c>, and the set holds
    /// no document for it either.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_entity_wrapping_the_wrong_emoji_is_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync("🦄", [CustomEmoji(0, 2, KnownDocumentId)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        result.Entities.ShouldBeNull();
    }

    /// <summary>
    /// Where the referenced set does hold the emoji under another document, the id is repointed instead
    /// of dropped, so a client working from an older copy of the set still gets its custom emoji.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_entity_wrapping_another_emoji_of_the_same_set_is_repointed()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync(Heart, [CustomEmoji(0, Heart.Length, KnownDocumentId)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        var entities = result.Entities.ShouldNotBeNull();
        entities.Count.ShouldBe(1);
        entities[0].ShouldBeOfType<TMessageEntityCustomEmoji>().DocumentId.ShouldBe(SiblingDocumentId);
    }

    /// <summary>
    /// Past <c>message_animated_emoji_max</c> the extra entities are dropped, in reading order, rather
    /// than the send being refused.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Custom_emojis_past_message_animated_emoji_max_are_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database);
        var service = CreateService(mongo.Database, animatedEmojiMax: 3);

        var text = string.Concat(Enumerable.Repeat(Grin, 5));
        var entities = Enumerable.Range(0, 5)
            .Select(index => CustomEmoji(index * Grin.Length, Grin.Length, KnownDocumentId))
            .Cast<IMessageEntity>()
            .ToList();

        var result = await service.ProcessAsync(text, entities,
            options: MessageEntityProcessingOptions.ValidateOnly);

        var processed = result.Entities.ShouldNotBeNull();
        processed.Count.ShouldBe(3);
        processed.Select(x => x.Offset).ShouldBe([0, Grin.Length, Grin.Length * 2]);
    }

    /// <summary>
    /// The stickerset reference behind a custom emoji is stored as <c>_id</c> when it is written through
    /// the model and as <c>Id</c> by the older seeder rows — the driver maps a member named <c>Id</c> to
    /// the <c>_id</c> element even inside a subdocument. Reading only one of the two yields
    /// <c>inputStickerSetID(id = 0)</c>, which nothing notices until the pack fails to open.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_stickerset_reference_stored_as_underscore_id_is_still_resolved()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedAsync(mongo.Database, stickerSetIdField: "_id");
        var service = CreateService(mongo.Database);

        var result = await service.ProcessAsync(Heart, [CustomEmoji(0, Heart.Length, KnownDocumentId)],
            options: MessageEntityProcessingOptions.ValidateOnly);

        var entities = result.Entities.ShouldNotBeNull();
        entities[0].ShouldBeOfType<TMessageEntityCustomEmoji>().DocumentId.ShouldBe(SiblingDocumentId);
    }

    private static TMessageEntityCustomEmoji CustomEmoji(int offset, int length, long documentId)
    {
        return new TMessageEntityCustomEmoji { Offset = offset, Length = length, DocumentId = documentId };
    }

    private static IMessageEntityService CreateService(IMongoDatabase database, int animatedEmojiMax = 100)
    {
        var appConfigHelper = new Mock<IAppConfigHelper>(MockBehavior.Loose);
        appConfigHelper.Setup(p => p.GetAppConfig()).Returns(new TJsonObject
        {
            Value =
            [
                new TJsonObjectValue
                {
                    Key = "message_animated_emoji_max",
                    Value = new TJsonNumber { Value = animatedEmojiMax }
                }
            ]
        });

        return new MessageEntityService(
            database,
            new Mock<IPeerHelper>(MockBehavior.Loose).Object,
            new Mock<IUserAppService>(MockBehavior.Loose).Object,
            new Mock<IQueryProcessor>(MockBehavior.Loose).Object,
            appConfigHelper.Object);
    }
}
