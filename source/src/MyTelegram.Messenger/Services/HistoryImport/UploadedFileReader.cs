using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reads back a file a client uploaded with <c>upload.saveFilePart</c> /
/// <c>upload.saveBigFilePart</c>, which stages the parts in the <c>file_parts</c> collection keyed by
/// the uploader.
/// </summary>
public static class UploadedFileReader
{
    public const string CollectionName = "file_parts";

    /// <summary>
    /// Concatenates the parts of an uploaded file. Returns null when the upload is missing, when a
    /// part is missing from the sequence, or when the body is larger than <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(IMongoDatabase database, long userId, IInputFile file,
        long maxBytes = long.MaxValue)
    {
        var (fileId, expectedParts) = file switch
        {
            TInputFile inputFile => (inputFile.Id, inputFile.Parts),
            TInputFileBig inputFileBig => (inputFileBig.Id, inputFileBig.Parts),
            _ => (0L, 0)
        };

        if (fileId == 0 || expectedParts <= 0)
        {
            return null;
        }

        var parts = await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("FileId", fileId)))
            .Sort(Builders<BsonDocument>.Sort.Ascending("FilePart"))
            .ToListAsync();

        if (parts.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        var expectedIndex = 0;
        foreach (var part in parts)
        {
            if (!part.TryGetValue("Bytes", out var bytesValue) || !bytesValue.IsBsonBinaryData)
            {
                return null;
            }

            // A gap in the sequence would silently splice two halves of a file together.
            if (part.TryGetValue("FilePart", out var indexValue) && indexValue.ToInt32() != expectedIndex)
            {
                return null;
            }

            expectedIndex++;

            var bytes = bytesValue.AsBsonBinaryData.Bytes;
            if (stream.Length + bytes.Length > maxBytes)
            {
                return null;
            }

            stream.Write(bytes, 0, bytes.Length);
        }

        return stream.ToArray();
    }

    /// <summary>Drops the staged parts of an upload that has been consumed.</summary>
    public static Task DeletePartsAsync(IMongoDatabase database, long userId, IInputFile file)
    {
        var fileId = file switch
        {
            TInputFile inputFile => inputFile.Id,
            TInputFileBig inputFileBig => inputFileBig.Id,
            _ => 0L
        };

        if (fileId == 0)
        {
            return Task.CompletedTask;
        }

        return database.GetCollection<BsonDocument>(CollectionName)
            .DeleteManyAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("FileId", fileId)));
    }
}
