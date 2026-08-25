using Microsoft.Extensions.DependencyInjection;
using MyTelegram.Core;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Messenger.Services.Gifs;
using MyTelegram.Messenger.Services.VideoProcessing;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: turning an MPEG4 the server produced — a converted <c>image/gif</c>, or a GIF imported from
/// Tenor search — into a real document.
///
/// <para>
/// The document has to describe the file as an animation: <c>documentAttributeAnimated</c> plus
/// <c>video/mp4</c> is what makes it a GIF, and the saved-GIF list is validated against the read model, so
/// a document without it cannot be saved or re-sent. Neither route a client upload takes produces that —
/// the file server's <c>SaveMedia</c> only merges parts it received itself, and its <c>CreateDocument</c>
/// hardcodes sticker attributes — so the row is written here and this test is what keeps it readable.
/// </para>
/// </summary>
public class GifDocumentPublisherTests
{
    private const long UserId = 2_000_001;

    [RequiresMongoDbFact]
    public async Task A_published_animation_reads_back_as_an_mpeg4_gif()
    {
        RegisterSerializers();

        using var mongo = EmbeddedMongoServer.Start();
        var path = await WriteTempFileAsync(4096);

        try
        {
            var reader = new GifDocumentReader(mongo.Database, Mapper());
            var publisher = new GifDocumentPublisher(mongo.Database, Storage(), reader, Transcoder(),
                NullLogger<GifDocumentPublisher>.Instance);

            var document = await publisher.PublishAsync(UserId, path, "cat.mp4",
                new VideoInfo(320, 240, 3, "h264"));

            document.ShouldNotBeNull();

            var stored = await reader.GetAsync(document!.Id);
            stored.ShouldNotBeNull();
            stored!.MimeType.ShouldBe("video/mp4");
            stored.Size.ShouldBe(4096);
            stored.CreatorId.ShouldBe(UserId);
            // Server-made bodies are unencrypted and live on the media DC, like sticker files.
            stored.DcId.ShouldBe(MyTelegramConsts.MediaDcId);
            stored.AccessHash.ShouldBeGreaterThan(0);
            // A client treats an empty file reference as stale and refreshes instead of downloading.
            stored.FileReference.Length.ShouldBeGreaterThan(0);

            // The property everything else depends on.
            GifDocumentHelper.IsAnimatedMp4(stored).ShouldBeTrue();

            var video = stored.Attributes2!.OfType<TDocumentAttributeVideo>().ShouldHaveSingleItem();
            video.W.ShouldBe(320);
            video.H.ShouldBe(240);
            video.Nosound.ShouldBeTrue();

            stored.Attributes2!.OfType<TDocumentAttributeFilename>().ShouldHaveSingleItem()
                .FileName.ShouldBe("cat.mp4");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A body that could not be stored must not leave a document behind: the row would describe a file
    /// that cannot be downloaded, and clients would keep it in their saved GIFs forever.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Nothing_is_published_when_the_body_cannot_be_stored()
    {
        RegisterSerializers();

        using var mongo = EmbeddedMongoServer.Start();
        var path = await WriteTempFileAsync(128);

        var storage = new Mock<IStoredFileStorage>(MockBehavior.Loose);
        storage.Setup(p => p.UploadFileAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new IOException("the object store is unreachable"));

        try
        {
            var reader = new GifDocumentReader(mongo.Database, Mapper());
            var publisher = new GifDocumentPublisher(mongo.Database, storage.Object, reader, Transcoder(),
                NullLogger<GifDocumentPublisher>.Instance);

            (await publisher.PublishAsync(UserId, path, "cat.mp4", null)).ShouldBeNull();

            (await mongo.Database
                .GetCollection<MongoDB.Bson.BsonDocument>("eventflow-documentreadmodel")
                .CountDocumentsAsync(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Empty))
                .ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RequiresMongoDbFact]
    public async Task An_empty_file_is_refused()
    {
        RegisterSerializers();

        using var mongo = EmbeddedMongoServer.Start();
        var path = await WriteTempFileAsync(0);

        try
        {
            var publisher = new GifDocumentPublisher(mongo.Database, Storage(),
                new GifDocumentReader(mongo.Database, Mapper()), Transcoder(),
                NullLogger<GifDocumentPublisher>.Instance);

            (await publisher.PublishAsync(UserId, path, "cat.mp4", null)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// With ffmpeg present the still frame becomes <c>document.thumbs</c> and is stored as the
    /// <c>{fileId}_m</c> object the file server serves thumbnails from. Without it a client has nothing to
    /// draw until the whole animation has arrived, which is what "the media loads forever" looks like.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_preview_frame_is_stored_and_described()
    {
        RegisterSerializers();

        using var mongo = EmbeddedMongoServer.Start();
        var path = await WriteTempFileAsync(2048);

        var storage = new Mock<IStoredFileStorage>(MockBehavior.Loose);
        storage.Setup(p => p.UploadFileAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var transcoder = new Mock<IVideoTranscoder>(MockBehavior.Loose);
        transcoder.Setup(p => p.ExtractThumbnailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string destination, int _, CancellationToken _) =>
            {
                File.WriteAllBytes(destination, new byte[512]);

                return Task.FromResult(true);
            });
        transcoder.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoInfo(320, 180, 0, "mjpeg"));

        try
        {
            var reader = new GifDocumentReader(mongo.Database, Mapper());
            var publisher = new GifDocumentPublisher(mongo.Database, storage.Object, reader, transcoder.Object,
                NullLogger<GifDocumentPublisher>.Instance);

            var document = await publisher.PublishAsync(UserId, path, "cat.mp4",
                new VideoInfo(640, 360, 2, "h264"));
            document.ShouldNotBeNull();

            var stored = (await reader.GetAsync(document!.Id))!;
            var thumb = stored.Thumbs.ShouldNotBeNull().ShouldHaveSingleItem();
            thumb.W.ShouldBe(320);
            thumb.H.ShouldBe(180);
            thumb.Type.ShouldBe("m");
            thumb.Size.ShouldBe(512);

            storage.Verify(p => p.UploadFileAsync(document.Id, It.IsAny<string>(), It.IsAny<CancellationToken>(),
                "m"), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// ffmpeg is not run in a unit test, so the preview is reported as unavailable — the publisher has to
    /// carry on without one.
    /// </summary>
    private static IVideoTranscoder Transcoder()
    {
        var transcoder = new Mock<IVideoTranscoder>(MockBehavior.Loose);
        transcoder.Setup(p => p.ExtractThumbnailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        return transcoder.Object;
    }

    private static IStoredFileStorage Storage()
    {
        var storage = new Mock<IStoredFileStorage>(MockBehavior.Loose);
        storage.Setup(p => p.UploadFileAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return storage.Object;
    }

    /// <summary>
    /// Only the identity of the mapped document is used by the assertions; the mapping itself is the
    /// ordinary read-model-to-TL one and is not what this test is about.
    /// </summary>
    private static IObjectMapper Mapper()
    {
        var mapper = new Mock<IObjectMapper>(MockBehavior.Loose);
        mapper.Setup(p => p.Map<IDocumentReadModel, TDocument>(It.IsAny<IDocumentReadModel>()))
            .Returns((IDocumentReadModel model) => new TDocument
            {
                Id = model.DocumentId,
                AccessHash = model.AccessHash,
                MimeType = model.MimeType,
                Size = model.Size,
                DcId = model.DcId,
                Date = model.Date,
                FileReference = model.FileReference,
                Attributes = new TVector<IDocumentAttribute>(model.Attributes2 ?? []),
                Thumbs = new TVector<IPhotoSize>(),
                VideoThumbs = new TVector<IVideoSize>()
            });

        return mapper.Object;
    }

    private static async Task<string> WriteTempFileAsync(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gif-publisher-{Guid.NewGuid():N}.mp4");
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);

        return path;
    }

    private static void RegisterSerializers()
    {
        // The read model carries `List<IDocumentAttribute>`, which only deserializes once the
        // discriminator conventions this server registers at startup are in place.
        new ServiceCollection().RegisterMongoDbSerializer();
    }
}
