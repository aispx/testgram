using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reads back the chat export file a client uploaded before calling
/// <c>messages.initHistoryImport</c>. See https://corefork.telegram.org/api/import
/// </summary>
public interface IHistoryImportFileReader
{
    /// <summary>
    /// Returns the body of an uploaded file, or null when the upload cannot be found or is larger
    /// than <paramref name="maxBytes"/>.
    /// </summary>
    Task<byte[]?> ReadAsync(long userId, IInputFile file, string fileName, long maxBytes,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Where an upload lands depends on which server answered <c>upload.saveFilePart</c>: the messenger's
/// own handler stages the parts in the <c>file_parts</c> collection, while the external file server
/// keeps them itself and only materializes a body once it is asked to create a document. Both are in
/// use, so both are tried, cheapest first.
/// </remarks>
public class HistoryImportFileReader(
    IMongoDatabase mongoDatabase,
    IMediaHelper mediaHelper,
    IStoredFileStorage storedFileStorage,
    ILogger<HistoryImportFileReader> logger)
    : IHistoryImportFileReader, ITransientDependency
{
    public async Task<byte[]?> ReadAsync(long userId, IInputFile file, string fileName, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var staged = await UploadedFileReader.ReadAsync(mongoDatabase, userId, file, maxBytes);
        if (staged != null)
        {
            return staged;
        }

        return await ReadThroughFileServerAsync(file, fileName, maxBytes, cancellationToken);
    }

    /// <summary>
    /// Turns the upload into a document on the file server, which writes the body into the object
    /// store, and reads it back from there. The document is the export file itself, which the server
    /// is expected to keep anyway.
    /// </summary>
    private async Task<byte[]?> ReadThroughFileServerAsync(IInputFile file, string fileName, long maxBytes,
        CancellationToken cancellationToken)
    {
        IMessageMedia? media;
        try
        {
            media = await mediaHelper.SaveMediaAsync(new TInputMediaUploadedDocument
            {
                File = file,
                MimeType = "text/plain",
                ForceFile = true,
                Attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeFilename
                {
                    FileName = string.IsNullOrWhiteSpace(fileName) ? "chat.txt" : fileName
                })
            });
        }
        catch (Exception ex)
        {
            // The upload is simply not there; the caller reports IMPORT_FILE_INVALID.
            logger.LogWarning(ex, "The chat export file could not be stored by the file server");
            return null;
        }

        if (media is not TMessageMediaDocument { Document: TDocument document })
        {
            logger.LogWarning("The file server did not return a document for the chat export file");
            return null;
        }

        if (document.Size > maxBytes)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"history-import-{document.Id}.txt");
        try
        {
            if (!await storedFileStorage.DownloadToFileAsync(document.Id, path, cancellationToken))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > maxBytes)
            {
                return null;
            }

            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
