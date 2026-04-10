using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Search for stickers using AI-powered keyword search
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchStickersHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearchStickers, MyTelegram.Schema.Messages.IFoundStickers>
{
    protected override async Task<MyTelegram.Schema.Messages.IFoundStickers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSearchStickers obj)
    {
        var emoticon = obj.Emoticon?.Trim() ?? "";

        if (string.IsNullOrEmpty(emoticon))
        {
            return new TFoundStickers { Stickers = new TVector<IDocument>() };
        }

        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        // Search in Packs by emoticon
        var filter = Builders<BsonDocument>.Filter.ElemMatch<BsonValue>("Packs",
            new BsonDocument("Emoticon", emoticon));

        var sets = await setCol.Find(filter).Limit(10).ToListAsync();

        var documentIds = new List<long>();

        foreach (var setDoc in sets)
        {
            if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
            {
                foreach (var pack in setDoc["Packs"].AsBsonArray)
                {
                    var packDoc = pack.AsBsonDocument;
                    if (packDoc.Contains("Emoticon") && packDoc["Emoticon"].AsString == emoticon && packDoc.Contains("Documents"))
                    {
                        var docs = packDoc["Documents"].AsBsonArray;
                        documentIds.AddRange(docs.Select(d => GetInt64(d)));
                    }
                }
            }
        }

        if (documentIds.Count == 0)
        {
            return new TFoundStickers { Stickers = new TVector<IDocument>() };
        }

        // Get documents
        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId",
            documentIds.Distinct().Select(id => (BsonValue)new BsonInt64(id)));
        var documents = await docCol.Find(docFilter).Limit(20).ToListAsync();

        var stickers = new List<IDocument>();

        foreach (var docBson in documents)
        {
            stickers.Add(BuildDocument(docBson));
        }

        return new TFoundStickers
        {
            Stickers = new TVector<IDocument>(stickers),
            Hash = 0
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
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
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