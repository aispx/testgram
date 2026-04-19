using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickers »</a>.Returns a list of <a href="https://corefork.telegram.org/constructor/document">documents</a> with the animated custom emoji in TGS format, and a <a href="https://corefork.telegram.org/constructor/documentAttributeCustomEmoji">documentAttributeCustomEmoji</a> attribute with the original emoji and info about the emoji stickerset this custom emoji belongs to.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getCustomEmojiDocuments"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetCustomEmojiDocumentsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments, TVector<MyTelegram.Schema.IDocument>>
{
    protected override async Task<TVector<MyTelegram.Schema.IDocument>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments obj)
    {
        if (obj.DocumentId == null || obj.DocumentId.Count == 0)
        {
            return [];
        }

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var filter = Builders<BsonDocument>.Filter.In("DocumentId", obj.DocumentId.Select(x => (BsonValue)new BsonInt64(x)));
        var docs = await docCol.Find(filter).ToListAsync();
        var docMap = docs.ToDictionary(d => GetInt64(d["DocumentId"]));
        var result = new List<IDocument>();

        foreach (var documentId in obj.DocumentId)
        {
            if (!docMap.TryGetValue(documentId, out var d))
            {
                continue;
            }

            result.Add(BuildDocument(d));
        }

        return new TVector<IDocument>(result);
    }

    private static IDocument BuildDocument(BsonDocument d)
    {
        byte[] fileRef = [];
        if (d.Contains("FileReference") && !d["FileReference"].IsBsonNull)
        {
            var fr = d["FileReference"];
            if (fr.BsonType == BsonType.Binary)
                fileRef = fr.AsBsonBinaryData.Bytes;
            else if (fr.BsonType == BsonType.Array)
                fileRef = fr.AsBsonArray.Select(x => (byte)GetInt32(x)).ToArray();
        }

        TVector<IDocumentAttribute> attributes = [];
        if (d.Contains("Attributes2") && !d["Attributes2"].IsBsonNull)
        {
            try
            {
                attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(d["Attributes2"].ToJson());
            }
            catch
            {
                attributes = [];
            }
        }

        return new TDocument
        {
            Id = GetInt64(d["DocumentId"]),
            AccessHash = GetInt64(d["AccessHash"]),
            FileReference = fileRef,
            Date = d.Contains("Date") ? GetInt32(d["Date"]) : 0,
            MimeType = d.Contains("MimeType") ? d["MimeType"].AsString : "application/octet-stream",
            Size = d.Contains("Size") ? GetInt64(d["Size"]) : 0,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = d.Contains("DcId") ? GetInt32(d["DcId"]) : 0,
            Attributes = attributes
        };
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }

    private static int GetInt32(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => (int)v.AsInt64,
            BsonType.Double => (int)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int32")
        };
    }
}