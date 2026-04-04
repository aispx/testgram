using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get recent stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getRecentStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRecentStickersHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetRecentStickers, MyTelegram.Schema.Messages.IRecentStickers>
{
    protected override async Task<MyTelegram.Schema.Messages.IRecentStickers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetRecentStickers obj)
    {
        var recentCol = mongoDatabase.GetCollection<BsonDocument>("recent_stickers");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("Attached", obj.Attached)
        );

        var recentDocs = await recentCol
            .Find(filter)
            .SortByDescending(x => x["Date"])
            .Limit(20)
            .ToListAsync();

        if (recentDocs.Count == 0)
        {
            return new TRecentStickers { Dates = [], Packs = [], Stickers = [] };
        }

        var docIds = recentDocs.Select(r => GetInt64(r["DocumentId"])).ToList();
        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", docIds.Select(id => (BsonValue)new BsonInt64(id)));
        var documents = await docCol.Find(docFilter).ToListAsync();
        var docMap = documents.ToDictionary(d => GetInt64(d["DocumentId"]));

        var stickers = new List<IDocument>();
        var dates = new List<int>();
        var packs = new List<IStickerPack>();

        foreach (var recentDoc in recentDocs)
        {
            var docId = GetInt64(recentDoc["DocumentId"]);
            if (docMap.TryGetValue(docId, out var doc))
            {
                stickers.Add(BuildDocument(doc));
                dates.Add(GetInt32(recentDoc["Date"]));
            }
        }

        return new TRecentStickers
        {
            Stickers = new TVector<IDocument>(stickers),
            Dates = new TVector<int>(dates),
            Packs = new TVector<IStickerPack>(packs)
        };
    }

    private IDocument BuildDocument(BsonDocument docBson)
    {
        var docId = GetInt64(docBson["DocumentId"]);
        var accessHash = GetInt64(docBson["AccessHash"]);
        var mimeType = docBson.Contains("MimeType") ? docBson["MimeType"].AsString : "application/octet-stream";
        var size = GetInt64(docBson["Size"]);
        var dcId = GetInt32(docBson["DcId"]);

        byte[] fileRef = [];
        if (docBson.Contains("FileReference") && !docBson["FileReference"].IsBsonNull)
        {
            var fr = docBson["FileReference"];
            if (fr.BsonType == BsonType.Binary)
                fileRef = fr.AsBsonBinaryData.Bytes;
            else if (fr.BsonType == BsonType.Array)
                fileRef = fr.AsBsonArray.Select(x => (byte)GetInt32(x)).ToArray();
        }

        TVector<IDocumentAttribute> attributes;
        if (docBson.Contains("Attributes2") && !docBson["Attributes2"].IsBsonNull)
        {
            try
            {
                attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(docBson["Attributes2"].ToJson());
            }
            catch
            {
                attributes = [];
            }
        }
        else
        {
            attributes = [];
        }

        return new TDocument
        {
            Id = docId,
            AccessHash = accessHash,
            FileReference = fileRef,
            Date = GetInt32(docBson["Date"]),
            MimeType = mimeType,
            Size = size,
            Thumbs = [],
            VideoThumbs = [],
            DcId = dcId,
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