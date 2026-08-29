using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Effects;

/// <summary>
/// Tests for <c>GetAvailableEffectsHandler</c> — the <c>messages.getAvailableEffects</c> RPC
/// (see https://corefork.telegram.org/api/effects).
///
/// <para>The handler is <c>internal sealed</c> and <c>HandleCoreAsync</c> is <c>protected</c>, so it is
/// reached by reflection, matching the approach already used by the stats handler smoke tests.</para>
/// </summary>
public class GetAvailableEffectsHandlerTests
{
    private const string HandlerNamespace = "MyTelegram.Messenger.Handlers.LatestLayer.Messages";
    private const long CallerUserId = 2010001;

    [RequiresMongoDbFact]
    public async Task Catalog_is_returned_with_every_referenced_document()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 1, emoticon: "🔥", order: 0);
        await InsertEffectAsync(mongo.Database, effectId: 2, emoticon: "👍", order: 1);

        var result = await InvokeAsync(mongo.Database, hash: 0);

        var effects = result.ShouldBeOfType<TAvailableEffects>();
        effects.Effects.Count.ShouldBe(2);
        effects.Documents.Count.ShouldBe(6);   // 3 documents per effect, none shared here
        effects.Hash.ShouldNotBe(0);

        var first = effects.Effects[0].ShouldBeOfType<TAvailableEffect>();
        first.Id.ShouldBe(1L);
        first.Emoticon.ShouldBe("🔥");
        first.EffectStickerId.ShouldBe(11L);
        first.StaticIconId.ShouldBe(12L);
        first.EffectAnimationId.ShouldBe(13L);

        // Every id on availableEffect must resolve inside the documents vector of the same response.
        var documentIds = effects.Documents.Cast<TDocument>().Select(p => p.Id).ToHashSet();
        foreach (var effect in effects.Effects.Cast<TAvailableEffect>())
        {
            documentIds.ShouldContain(effect.EffectStickerId);
            if (effect.StaticIconId.HasValue) documentIds.ShouldContain(effect.StaticIconId.Value);
            if (effect.EffectAnimationId.HasValue) documentIds.ShouldContain(effect.EffectAnimationId.Value);
        }

        Should.NotThrow(() => result.ToBytes()).Length.ShouldBeGreaterThan(0);
    }

    [RequiresMongoDbFact]
    public async Task Shared_documents_are_not_duplicated()
    {
        using var mongo = EmbeddedMongoServer.Start();

        // Two effects pointing at the very same sticker document.
        var sticker = CreateDocument(500);
        foreach (var (effectId, order) in new[] { (1L, 0), (2L, 1) })
        {
            await mongo.Database.GetCollection<BsonDocument>("effects").InsertOneAsync(new BsonDocument
            {
                { "EffectId", effectId },
                { "Emoticon", "🔥" },
                { "PremiumRequired", false },
                { "Order", order },
                { "EffectSticker", sticker },
                { "StaticIcon", BsonNull.Value },
                { "EffectAnimation", BsonNull.Value }
            });
        }

        var effects = (await InvokeAsync(mongo.Database, hash: 0)).ShouldBeOfType<TAvailableEffects>();

        effects.Effects.Count.ShouldBe(2);
        effects.Documents.Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Matching_hash_yields_availableEffectsNotModified()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 1, emoticon: "🔥", order: 0);

        var full = (await InvokeAsync(mongo.Database, hash: 0)).ShouldBeOfType<TAvailableEffects>();
        var cached = await InvokeAsync(mongo.Database, hash: full.Hash);

        cached.ShouldBeOfType<TAvailableEffectsNotModified>();
    }

    [RequiresMongoDbFact]
    public async Task Stale_hash_yields_the_full_catalog()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 1, emoticon: "🔥", order: 0);

        var result = await InvokeAsync(mongo.Database, hash: 12345);

        result.ShouldBeOfType<TAvailableEffects>().Effects.Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Empty_catalog_returns_initialized_vectors()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var effects = (await InvokeAsync(mongo.Database, hash: 0)).ShouldBeOfType<TAvailableEffects>();

        // Null vectors crash clients, so both must be present even with nothing to serve.
        effects.Effects.ShouldNotBeNull();
        effects.Documents.ShouldNotBeNull();
        effects.Effects.ShouldBeEmpty();
        effects.Documents.ShouldBeEmpty();
        Should.NotThrow(() => ((IObject)effects).ToBytes());
    }

    [RequiresMongoDbFact]
    public async Task Tgs_documents_carry_the_attributes_clients_need_to_render_them()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 1, emoticon: "🔥", order: 0);

        var effects = (await InvokeAsync(mongo.Database, hash: 0)).ShouldBeOfType<TAvailableEffects>();
        var document = effects.Documents.Cast<TDocument>().First();

        document.MimeType.ShouldBe("application/x-tgsticker");
        document.Attributes.ShouldContain(p => p is TDocumentAttributeImageSize);
        document.Attributes.ShouldContain(p => p is TDocumentAttributeFilename);
        document.Thumbs.ShouldNotBeNull();
        document.VideoThumbs.ShouldNotBeNull();
        document.AccessHash.ShouldNotBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Premium_required_flag_is_carried_through_to_the_client()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertEffectAsync(mongo.Database, effectId: 1, emoticon: "🔥", order: 0,
            premiumRequired: true);
        await InsertEffectAsync(mongo.Database, effectId: 2, emoticon: "👍", order: 1,
            premiumRequired: false);

        var effects = (await InvokeAsync(mongo.Database, hash: 0)).ShouldBeOfType<TAvailableEffects>();
        var byId = effects.Effects.Cast<TAvailableEffect>().ToDictionary(p => p.Id);

        byId[1].PremiumRequired.ShouldBeTrue();
        byId[2].PremiumRequired.ShouldBeFalse();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<IObject> InvokeAsync(IMongoDatabase database, int hash)
    {
        var userAppService = new Mock<IUserAppService>();
        userAppService
            .Setup(p => p.CheckAccountPremiumStatusAsync(It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var accessHashHelper = new Mock<IAccessHashHelper2>();
        accessHashHelper
            .Setup(p => p.GenerateAccessHash(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<AccessHashType>()))
            .Returns<long, long, long, AccessHashType>((_, _, targetId, _) => targetId * 7 + 1);

        var effectAppService = new MessageEffectAppService(database, userAppService.Object);

        var assembly = typeof(MessageEffectAppService).Assembly;
        var handlerType = assembly.GetType($"{HandlerNamespace}.GetAvailableEffectsHandler", throwOnError: true)!;
        var handler = Activator.CreateInstance(
            handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [effectAppService, accessHashHelper.Object, TestFileReferences.Helper],
            culture: null)!;

        var method = handlerType.GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(CallerUserId);
        input.SetupGet(x => x.AccessHashKeyId).Returns(1234);

        var request = new MyTelegram.Schema.Messages.RequestGetAvailableEffects { Hash = hash };

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, [input.Object, request])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;
        return (IObject)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static Task InsertEffectAsync(
        IMongoDatabase database,
        long effectId,
        string emoticon,
        int order,
        bool premiumRequired = false)
    {
        var doc = new BsonDocument
        {
            { "EffectId", effectId },
            { "Emoticon", emoticon },
            { "PremiumRequired", premiumRequired },
            { "Order", order },
            { "EffectSticker", CreateDocument(effectId * 10 + 1) },
            { "StaticIcon", CreateDocument(effectId * 10 + 2) },
            { "EffectAnimation", CreateDocument(effectId * 10 + 3) }
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
