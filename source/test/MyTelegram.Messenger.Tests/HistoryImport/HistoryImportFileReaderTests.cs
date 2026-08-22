using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.VideoProcessing;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages — getting the uploaded export file back.
///
/// <para>
/// Which server answered <c>upload.saveFilePart</c> decides where the body is: the messenger stages the
/// parts in the <c>file_parts</c> collection, while the external file server keeps them to itself and
/// only writes a body into the object store once it is asked to create a document. Reading only one of
/// the two answered <c>IMPORT_FILE_INVALID</c> for every real upload.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class HistoryImportFileReaderTests
{
    private const long UserId = 2010001;
    private const long FileId = 5555;
    private const long DocumentId = 987654;

    private static readonly IInputFile File = new TInputFile
    {
        Id = FileId,
        Parts = 1,
        Name = "_chat.txt",
        Md5Checksum = string.Empty
    };

    [RequiresMongoDbFact]
    public async Task Parts_staged_in_mongo_are_read_without_touching_the_file_server()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await StagePartsAsync(mongo.Database, "hello"u8.ToArray());

        var mediaHelper = new Mock<IMediaHelper>(MockBehavior.Loose);
        var reader = CreateReader(mongo.Database, mediaHelper, storage: null);

        var bytes = await reader.ReadAsync(UserId, File, "_chat.txt", 1024);

        Encoding.UTF8.GetString(bytes!).ShouldBe("hello");
        mediaHelper.Verify(p => p.SaveMediaAsync(It.IsAny<IInputMedia>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task An_upload_held_by_the_file_server_is_materialized_and_read_back()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var mediaHelper = new Mock<IMediaHelper>(MockBehavior.Loose);
        mediaHelper.Setup(p => p.SaveMediaAsync(It.IsAny<IInputMedia>()))
            .ReturnsAsync(new TMessageMediaDocument
            {
                Document = new TDocument
                {
                    Id = DocumentId,
                    AccessHash = 1,
                    FileReference = new byte[] { 1 },
                    Date = 0,
                    MimeType = "text/plain",
                    Size = 5,
                    DcId = 2,
                    Attributes = []
                }
            });

        var storage = new Mock<IStoredFileStorage>(MockBehavior.Loose);
        storage.Setup(p => p.DownloadToFileAsync(DocumentId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<long, string, CancellationToken>(async (_, path, _) =>
            {
                await System.IO.File.WriteAllTextAsync(path, "hello");
                return true;
            });

        var reader = CreateReader(mongo.Database, mediaHelper, storage);

        var bytes = await reader.ReadAsync(UserId, File, "_chat.txt", 1024);

        Encoding.UTF8.GetString(bytes!).ShouldBe("hello");

        // The export file is stored as a plain document, not as a sticker or a photo.
        mediaHelper.Verify(p => p.SaveMediaAsync(It.Is<IInputMedia>(m =>
            m is TInputMediaUploadedDocument && ((TInputMediaUploadedDocument)m).MimeType == "text/plain")),
            Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task An_upload_neither_side_knows_about_is_not_readable()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var mediaHelper = new Mock<IMediaHelper>(MockBehavior.Loose);
        mediaHelper.Setup(p => p.SaveMediaAsync(It.IsAny<IInputMedia>()))
            .ThrowsAsync(new RpcException(RpcErrors.RpcErrors400.FileIdInvalid));

        var reader = CreateReader(mongo.Database, mediaHelper, storage: null);

        (await reader.ReadAsync(UserId, File, "_chat.txt", 1024)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task A_body_above_the_cap_is_refused()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await StagePartsAsync(mongo.Database, new byte[2048]);

        var reader = CreateReader(mongo.Database, new Mock<IMediaHelper>(MockBehavior.Loose), storage: null);

        (await reader.ReadAsync(UserId, File, "_chat.txt", 1024)).ShouldBeNull();
    }

    private static HistoryImportFileReader CreateReader(IMongoDatabase database, Mock<IMediaHelper> mediaHelper,
        Mock<IStoredFileStorage>? storage)
    {
        return new HistoryImportFileReader(database, mediaHelper.Object,
            (storage ?? new Mock<IStoredFileStorage>(MockBehavior.Loose)).Object,
            NullLogger<HistoryImportFileReader>.Instance);
    }

    private static Task StagePartsAsync(IMongoDatabase database, byte[] bytes)
    {
        return database.GetCollection<BsonDocument>("file_parts").InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"{UserId}_{FileId}_0",
            ["UserId"] = UserId,
            ["FileId"] = FileId,
            ["FilePart"] = 0,
            ["Bytes"] = bytes,
            ["Size"] = bytes.Length
        });
    }
}
