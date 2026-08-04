using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Effects;

/// <summary>
/// Integration tests for <see cref="MessageEffectAppService"/> — the message effect catalog behind
/// <c>messages.getAvailableEffects</c> and the effect validation applied to outgoing messages
/// (see https://corefork.telegram.org/api/effects).
///
/// <para>The service reads the <c>effects</c> collection directly, and the exact BSON shapes it must
/// tolerate (Int32 vs Int64 ids, Binary vs Array file references, missing optional documents) are the
/// substance of the logic, so these run against a real <c>mongod</c> via
/// <see cref="EmbeddedMongoServer"/> rather than a mocked driver.</para>
/// </summary>
public class MessageEffectAppServiceTests
{
    // ---- catalog reading -----------------------------------------------------------------------

    [RequiresMongoDbFact]
    public async Task Effects_are_returned_in_order()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await InsertEffectAsync(db, effectId: 10, emoticon: "🔥", order: 2);
        await InsertEffectAsync(db, effectId: 20, emoticon: "👍", order: 0);
        await InsertEffectAsync(db, effectId: 30, emoticon: "❤", order: 1);

        var service = CreateService(db);
        var effects = await service.GetAllAsync();

        effects.Select(p => p.EffectId).ShouldBe([20L, 30L, 10L]);
        effects.Select(p => p.Emoticon).ShouldBe(["👍", "❤", "🔥"]);
    }

    [RequiresMongoDbFact]
    public async Task Optional_documents_are_read_when_present_and_null_otherwise()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await InsertEffectAsync(db, effectId: 1, emoticon: "🔥", order: 0,
            withStaticIcon: true, withEffectAnimation: true);
        await InsertEffectAsync(db, effectId: 2, emoticon: "👍", order: 1,
            withStaticIcon: false, withEffectAnimation: false);

        var service = CreateService(db);
        var effects = await service.GetAllAsync();

        var withOptional = effects.Single(p => p.EffectId == 1);
        withOptional.StaticIcon.ShouldNotBeNull();
        withOptional.EffectAnimation.ShouldNotBeNull();

        var withoutOptional = effects.Single(p => p.EffectId == 2);
        withoutOptional.StaticIcon.ShouldBeNull();
        withoutOptional.EffectAnimation.ShouldBeNull();

        // effect_sticker is mandatory for every effect that is served at all.
        withoutOptional.EffectSticker.ShouldNotBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Effect_without_effect_sticker_is_skipped_rather_than_served_broken()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await InsertEffectAsync(db, effectId: 1, emoticon: "🔥", order: 0);
        await db.GetCollection<BsonDocument>("effects").InsertOneAsync(new BsonDocument
        {
            { "EffectId", 2L },
            { "Emoticon", "👍" },
            { "PremiumRequired", false },
            { "Order", 1 },
            { "EffectSticker", BsonNull.Value }
        });

        var service = CreateService(db);
        var effects = await service.GetAllAsync();

        effects.Select(p => p.EffectId).ShouldBe([1L]);
    }

    [RequiresMongoDbFact]
    public async Task Int32_ids_and_array_file_references_are_tolerated()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        // A re-seed or a manual edit can leave an id as Int32 and a file reference as an array of
        // numbers instead of BSON binary; neither must break the catalog.
        await db.GetCollection<BsonDocument>("effects").InsertOneAsync(new BsonDocument
        {
            { "EffectId", 7 },
            { "Emoticon", "🎉" },
            { "PremiumRequired", false },
            { "Order", 0 },
            {
                "EffectSticker", new BsonDocument
                {
                    { "Id", 555 },
                    { "FileReference", new BsonArray { 1, 2, 3 } },
                    { "Date", 100 },
                    { "MimeType", "application/x-tgsticker" },
                    { "Size", 42 },
                    { "DcId", 2 }
                }
            }
        });

        var service = CreateService(db);
        var effect = (await service.GetAllAsync()).Single();

        effect.EffectId.ShouldBe(7L);
        effect.EffectSticker.DocumentId.ShouldBe(555L);
        effect.EffectSticker.FileReference.ShouldBe([(byte)1, (byte)2, (byte)3]);
        effect.EffectSticker.Thumbs.ShouldNotBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Empty_catalog_yields_no_effects_and_a_stable_hash()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        var service = CreateService(db);
        var effects = await service.GetAllAsync();

        effects.ShouldBeEmpty();
        service.GetHash(effects).ShouldBe(0);
    }

    // ---- hash ----------------------------------------------------------------------------------

    [RequiresMongoDbFact]
    public async Task Hash_changes_with_the_catalog_and_is_stable_for_the_same_catalog()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await InsertEffectAsync(db, effectId: 1, emoticon: "🔥", order: 0);
        var firstService = CreateService(db);
        var firstHash = firstService.GetHash(await firstService.GetAllAsync());

        // Same catalog, fresh service instance: the hash must be reproducible across processes,
        // otherwise clients would never get availableEffectsNotModified.
        var sameService = CreateService(db);
        sameService.GetHash(await sameService.GetAllAsync()).ShouldBe(firstHash);

        await InsertEffectAsync(db, effectId: 2, emoticon: "👍", order: 1);
        var changedService = CreateService(db);
        changedService.GetHash(await changedService.GetAllAsync()).ShouldNotBe(firstHash);

        firstHash.ShouldBeGreaterThanOrEqualTo(0);
    }

    // ---- lookup --------------------------------------------------------------------------------

    [RequiresMongoDbFact]
    public async Task Get_returns_the_effect_by_id_and_null_for_unknown_ids()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await InsertEffectAsync(db, effectId: 42, emoticon: "🔥", order: 0);

        var service = CreateService(db);

        (await service.GetAsync(42))!.Emoticon.ShouldBe("🔥");
        (await service.GetAsync(43)).ShouldBeNull();
    }

    // ---- validation ----------------------------------------------------------------------------

    [RequiresMongoDbFact]
    public async Task Null_and_zero_effect_ids_are_passed_through_as_no_effect()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongo.Database);

        (await service.ValidateEffectAsync(null, senderUserId: 1, PeerType.User)).ShouldBeNull();
        (await service.ValidateEffectAsync(0, senderUserId: 1, PeerType.User)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Valid_effect_in_a_private_chat_is_accepted()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 5, emoticon: "🔥", order: 0);

        var service = CreateService(mongo.Database);
        var result = await service.ValidateEffectAsync(5, senderUserId: 1, PeerType.User);

        result.ShouldBe(5L);
    }

    [RequiresMongoDbFact]
    public async Task Unknown_effect_id_is_rejected()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 5, emoticon: "🔥", order: 0);

        var service = CreateService(mongo.Database);

        var exception = await Should.ThrowAsync<Exception>(
            () => service.ValidateEffectAsync(999, senderUserId: 1, PeerType.User));
        exception.Message.ShouldContain("EFFECT_ID_INVALID");
    }

    [RequiresMongoDbTheory]
    [InlineData(PeerType.Channel)]
    [InlineData(PeerType.Chat)]
    public async Task Effects_are_dropped_outside_private_chats_instead_of_erroring(PeerType peerType)
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 5, emoticon: "🔥", order: 0);

        var service = CreateService(mongo.Database);

        // A client may keep an effect selected while switching to a group; erroring here would block
        // the send outright, which is not what the official server does.
        (await service.ValidateEffectAsync(5, senderUserId: 1, peerType)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Unknown_effect_id_outside_a_private_chat_is_dropped_without_a_lookup()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongo.Database);

        (await service.ValidateEffectAsync(999, senderUserId: 1, PeerType.Channel)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Premium_effect_requires_a_premium_account()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 5, emoticon: "🔥", order: 0,
            premiumRequired: true);

        var userAppService = new Mock<IUserAppService>();
        userAppService
            .Setup(p => p.CheckAccountPremiumStatusAsync(It.IsAny<long>()))
            .Throws(new Exception("PREMIUM_ACCOUNT_REQUIRED"));

        var service = new MessageEffectAppService(mongo.Database, userAppService.Object);

        var exception = await Should.ThrowAsync<Exception>(
            () => service.ValidateEffectAsync(5, senderUserId: 1, PeerType.User));
        exception.Message.ShouldContain("PREMIUM_ACCOUNT_REQUIRED");
    }

    [RequiresMongoDbFact]
    public async Task Premium_status_is_not_checked_for_a_free_effect()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 5, emoticon: "🔥", order: 0,
            premiumRequired: false);

        var userAppService = new Mock<IUserAppService>();
        var service = new MessageEffectAppService(mongo.Database, userAppService.Object);

        (await service.ValidateEffectAsync(5, senderUserId: 1, PeerType.User)).ShouldBe(5L);

        userAppService.Verify(p => p.CheckAccountPremiumStatusAsync(It.IsAny<long>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task Premium_effect_is_accepted_for_a_premium_account()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 5, emoticon: "🔥", order: 0,
            premiumRequired: true);

        var userAppService = new Mock<IUserAppService>();
        userAppService
            .Setup(p => p.CheckAccountPremiumStatusAsync(It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var service = new MessageEffectAppService(mongo.Database, userAppService.Object);

        (await service.ValidateEffectAsync(5, senderUserId: 7, PeerType.User)).ShouldBe(5L);
        userAppService.Verify(p => p.CheckAccountPremiumStatusAsync(7), Times.Once);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static IMessageEffectAppService CreateService(IMongoDatabase database)
    {
        var userAppService = new Mock<IUserAppService>();
        userAppService
            .Setup(p => p.CheckAccountPremiumStatusAsync(It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        return new MessageEffectAppService(database, userAppService.Object);
    }

    private static Task InsertEffectAsync(
        IMongoDatabase database,
        long effectId,
        string emoticon,
        int order,
        bool premiumRequired = false,
        bool withStaticIcon = true,
        bool withEffectAnimation = true)
    {
        var doc = new BsonDocument
        {
            { "EffectId", effectId },
            { "Emoticon", emoticon },
            { "PremiumRequired", premiumRequired },
            { "Order", order },
            { "EffectSticker", CreateDocument(effectId * 10 + 1) },
            { "StaticIcon", withStaticIcon ? CreateDocument(effectId * 10 + 2) : BsonNull.Value },
            {
                "EffectAnimation",
                withEffectAnimation ? CreateDocument(effectId * 10 + 3) : BsonNull.Value
            }
        };

        return database.GetCollection<BsonDocument>("effects").InsertOneAsync(doc);
    }

    private static BsonDocument CreateDocument(long documentId)
    {
        return new BsonDocument
        {
            { "Id", documentId },
            { "AccessHash", 123456789L },
            { "FileReference", new BsonBinaryData([1, 2, 3, 4]) },
            { "Date", 1700000000 },
            { "MimeType", "application/x-tgsticker" },
            { "Size", 2048L },
            { "DcId", 2 },
            {
                "Thumbs", new BsonArray
                {
                    new BsonDocument
                    {
                        { "_t", "TPhotoSize" }, { "Type", "m" },
                        { "W", 128 }, { "H", 128 }, { "Size", 512 }
                    }
                }
            }
        };
    }
}
