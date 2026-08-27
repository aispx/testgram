using System.Reflection;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Gifs;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: <c>messages.getSavedGifs</c> and <c>messages.saveGif</c>, the two halves of
/// <a href="https://corefork.telegram.org/api/gifs#saved-gifs">saved GIFs</a>.
///
/// <para>
/// Both handlers used to be stubs — one always answered with an empty list, the other accepted anything
/// and stored nothing. What matters now is that the list is real, that it is only ever made of MPEG4
/// animations (anything else is discarded by the clients, which desynchronises their list from ours), that
/// an unchanged list answers <c>savedGifsNotModified</c>, and that the other sessions are told to refetch.
/// </para>
/// </summary>
public class SavedGifsHandlerTests
{
    private const long UserId = 2_000_001;
    private const long GifId = 2_060_835_009_452_392_821;
    private const long OtherGifId = 122_669_354_334_652_493;
    private const long NotAGifId = 555;
    private const int Limit = 200;

    [RequiresMongoDbFact]
    public async Task An_empty_list_is_answered_with_an_empty_list_and_a_zero_hash()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        var result = await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs { Hash = 0 });

        var savedGifs = result.ShouldBeOfType<TSavedGifs>();
        savedGifs.Gifs.ShouldBeEmpty();
        savedGifs.Hash.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task A_saved_gif_comes_back_with_the_hash_the_client_will_compute()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));
        var result = await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs { Hash = 0 });

        var savedGifs = result.ShouldBeOfType<TSavedGifs>();
        savedGifs.Gifs.Count.ShouldBe(1);
        ((TDocument)savedGifs.Gifs[0]).Id.ShouldBe(GifId);
        savedGifs.Hash.ShouldBe(SavedGifHashHelper.ComputeHash([GifId]));
    }

    [RequiresMongoDbFact]
    public async Task The_newest_save_is_first_and_re_saving_moves_a_gif_back_to_the_front()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));
        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(OtherGifId));

        var afterSecondSave = await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs());
        Ids(afterSecondSave).ShouldBe([OtherGifId, GifId]);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));

        var afterReSave = await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs());
        Ids(afterReSave).ShouldBe([GifId, OtherGifId]);
    }

    [RequiresMongoDbFact]
    public async Task A_matching_hash_is_answered_with_savedGifsNotModified()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));
        var first = (TSavedGifs)await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs());

        var second = await InvokeAsync<ISavedGifs>(fixture.GetHandler,
            new RequestGetSavedGifs { Hash = first.Hash });

        second.ShouldBeOfType<TSavedGifsNotModified>();
    }

    [RequiresMongoDbFact]
    public async Task A_stale_hash_is_answered_with_the_whole_list()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));
        var first = (TSavedGifs)await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs());

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(OtherGifId));
        var second = await InvokeAsync<ISavedGifs>(fixture.GetHandler,
            new RequestGetSavedGifs { Hash = first.Hash });

        Ids(second).ShouldBe([OtherGifId, GifId]);
    }

    [RequiresMongoDbFact]
    public async Task A_zero_hash_always_gets_the_list_even_when_it_would_match()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        // A client that has nothing cached sends 0, and 0 is also the hash of an empty list, so it must
        // not be treated as "up to date".
        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));

        var result = await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs { Hash = 0 });

        result.ShouldBeOfType<TSavedGifs>().Gifs.Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Unsaving_removes_the_gif()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));
        var result = await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId, unsave: true));

        result.ShouldBeOfType<TBoolTrue>();
        Ids(await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs())).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Unsaving_something_that_was_never_saved_is_not_an_error()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        // tdlib and tdesktop read a false answer as "resync everything", so the only failure signal is
        // an RPC error - and there is nothing wrong here.
        var result = await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId, unsave: true));

        result.ShouldBeOfType<TBoolTrue>();
    }

    [RequiresMongoDbFact]
    public async Task Saving_a_document_that_is_not_a_gif_is_GIF_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(NotAGifId)));

        exception.RpcError.Message.ShouldBe("GIF_ID_INVALID");
        exception.RpcError.ErrorCode.ShouldBe(400);
    }

    [RequiresMongoDbFact]
    public async Task Saving_a_document_that_does_not_exist_is_GIF_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(987_654_321)));

        exception.RpcError.Message.ShouldBe("GIF_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task An_entry_that_stopped_being_a_gif_is_dropped_from_the_answer_and_from_storage()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new SavedGifStore(mongo.Database);
        var fixture = CreateFixture(mongo.Database, store);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));
        // Whatever the reason - a deleted file, a document that was replaced - the client would discard
        // it, leaving its list shorter than ours and the hash mismatched for good.
        await store.AddAsync(UserId, NotAGifId, Limit);

        var result = await InvokeAsync<ISavedGifs>(fixture.GetHandler, new RequestGetSavedGifs());

        Ids(result).ShouldBe([GifId]);
        (await store.GetOrderedIdsAsync(UserId, Limit)).ShouldBe([GifId]);
    }

    [RequiresMongoDbFact]
    public async Task Saving_tells_the_other_sessions_to_refetch()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync<IBool>(fixture.SaveHandler, SaveRequest(GifId));

        // "Modifying the saved gifs list [...] will emit an updateSavedGifs update to other currently
        // logged in sessions" - and only the others, the calling session already has the rpc result.
        fixture.Notifier.Verify(p => p.NotifyAsync(UserId, It.IsAny<long?>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task A_saveGif_with_a_bad_input_type_is_GIF_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync<IBool>(fixture.SaveHandler, new RequestSaveGif { Id = new TInputDocumentEmpty() }));

        exception.RpcError.Message.ShouldBe("GIF_ID_INVALID");
    }

    private static List<long> Ids(ISavedGifs result)
    {
        return result is TSavedGifs savedGifs
            ? savedGifs.Gifs.Cast<TDocument>().Select(p => p.Id).ToList()
            : [];
    }

    private static RequestSaveGif SaveRequest(long documentId, bool unsave = false)
    {
        return new RequestSaveGif
        {
            Id = new TInputDocument { Id = documentId, AccessHash = 4242, FileReference = new byte[] { 1, 2 } },
            Unsave = unsave
        };
    }

    private sealed record Fixture(object GetHandler, object SaveHandler, Mock<ISavedGifUpdateNotifier> Notifier);

    private static Fixture CreateFixture(IMongoDatabase database, SavedGifStore? store = null)
    {
        store ??= new SavedGifStore(database);

        var limitResolver = new Mock<ISavedGifLimitResolver>(MockBehavior.Loose);
        limitResolver.Setup(p => p.GetLimitAsync(It.IsAny<long>())).ReturnsAsync(Limit);

        var documentReader = new Mock<IGifDocumentReader>(MockBehavior.Loose);
        documentReader
            .Setup(p => p.GetAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, CancellationToken _) => ReadModel(id));
        documentReader
            .Setup(p => p.GetAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<long> ids, CancellationToken _) => ids
                .Select(ReadModel)
                .Where(p => p != null)
                .ToDictionary(p => p!.DocumentId, p => p!));
        documentReader
            .Setup(p => p.Map(It.IsAny<IDocumentReadModel>()))
            .Returns((IDocumentReadModel model) => new TDocument
            {
                Id = model.DocumentId,
                AccessHash = model.AccessHash,
                MimeType = model.MimeType,
                Attributes = [.. model.Attributes2 ?? []],
                Thumbs = new TVector<IPhotoSize>(),
                VideoThumbs = new TVector<IVideoSize>()
            });

        var conversionStore = new Mock<IGifMp4ConversionStore>(MockBehavior.Loose);
        conversionStore
            .Setup(p => p.GetMp4DocumentIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var notifier = new Mock<ISavedGifUpdateNotifier>(MockBehavior.Loose);

        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);
        accessHashHelper
            .Setup(p => p.CheckAccessHashAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<long>(),
                It.IsAny<long>(), It.IsAny<AccessHashType?>()))
            .Returns(Task.CompletedTask);

        var assembly = typeof(SavedGifStore).Assembly;

        var getHandler = Activator.CreateInstance(
            assembly.GetType("MyTelegram.Messenger.Handlers.LatestLayer.Messages.GetSavedGifsHandler", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [store, limitResolver.Object, documentReader.Object],
            culture: null)!;

        var saveHandler = Activator.CreateInstance(
            assembly.GetType("MyTelegram.Messenger.Handlers.LatestLayer.Messages.SaveGifHandler", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                store,
                limitResolver.Object,
                notifier.Object,
                documentReader.Object,
                conversionStore.Object,
                accessHashHelper.Object
            ],
            culture: null)!;

        return new Fixture(getHandler, saveHandler, notifier);
    }

    private static IDocumentReadModel? ReadModel(long documentId)
    {
        if (documentId is not (GifId or OtherGifId or NotAGifId))
        {
            return null;
        }

        var isGif = documentId != NotAGifId;
        var document = new Mock<IDocumentReadModel>(MockBehavior.Loose);
        document.SetupGet(p => p.DocumentId).Returns(documentId);
        document.SetupGet(p => p.AccessHash).Returns(4242);
        document.SetupGet(p => p.MimeType).Returns(isGif ? "video/mp4" : "image/png");
        document.SetupGet(p => p.Attributes2).Returns(isGif
            ? [new TDocumentAttributeAnimated()]
            : [new TDocumentAttributeImageSize { W = 1, H = 1 }]);

        return document.Object;
    }

    private static async Task<T> InvokeAsync<T>(object handler, IObject request)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(UserId);
        input.SetupGet(p => p.AuthKeyId).Returns(777);

        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

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

        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
