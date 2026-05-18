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
        var emoticon = obj.Emoticon?.Trim() ?? string.Empty;
        var query = obj.Q?.Trim().ToLowerInvariant() ?? string.Empty;
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, 100) : 20;

        if (string.IsNullOrEmpty(emoticon) && string.IsNullOrEmpty(query))
        {
            return new TFoundStickers { Stickers = new TVector<IDocument>(), Hash = 0 };
        }

        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var setDocs = await setCol.Find(FilterDefinition<BsonDocument>.Empty).Limit(200).ToListAsync();
        var documentIds = new List<long>();

        foreach (var setDoc in setDocs)
        {
            var isEmojiSet = setDoc.Contains("Emojis") && setDoc["Emojis"].ToBoolean();
            if (obj.Emojis && !isEmojiSet)
            {
                continue;
            }
            if (!obj.Emojis && isEmojiSet)
            {
                continue;
            }

            if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
            {
                foreach (var pack in setDoc["Packs"].AsBsonArray)
                {
                    var packDoc = pack.AsBsonDocument;
                    var packEmoticon = packDoc.Contains("Emoticon") ? packDoc["Emoticon"].AsString : string.Empty;
                    var emoticonMatch = !string.IsNullOrEmpty(emoticon) && string.Equals(packEmoticon, emoticon, StringComparison.Ordinal);
                    var queryMatch = !string.IsNullOrEmpty(query) && packEmoticon.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if ((emoticonMatch || queryMatch) && packDoc.Contains("Documents"))
                    {
                        documentIds.AddRange(packDoc["Documents"].AsBsonArray.Select(GetInt64));
                    }
                }
            }

            if (!string.IsNullOrEmpty(query) && setDoc.Contains("Keywords") && setDoc["Keywords"].IsBsonArray)
            {
                foreach (var keywordValue in setDoc["Keywords"].AsBsonArray)
                {
                    var keywordDoc = keywordValue.AsBsonDocument;
                    if (!keywordDoc.Contains("Keyword") || !keywordDoc["Keyword"].IsBsonArray)
                    {
                        continue;
                    }

                    var keywords = keywordDoc["Keyword"].AsBsonArray.Select(x => x.AsString.ToLowerInvariant()).ToList();
                    if (keywords.Any(x => x == query || x.Contains(query) || query.Contains(x)))
                    {
                        documentIds.Add(GetInt64(keywordDoc["DocumentId"]));
                    }
                }
            }
        }

        var distinctIds = documentIds.Distinct().Skip(obj.Offset).Take(limit).ToList();
        if (distinctIds.Count == 0)
        {
            return new TFoundStickers { Stickers = new TVector<IDocument>(), Hash = 0 };
        }

        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", distinctIds.Select(id => (BsonValue)new BsonInt64(id)));
        var documents = await docCol.Find(docFilter).ToListAsync();
        var docMap = documents.ToDictionary(x => GetInt64(x["DocumentId"]));
        var stickers = new List<IDocument>();
        foreach (var documentId in distinctIds)
        {
            if (docMap.TryGetValue(documentId, out var docBson))
            {
                stickers.Add(BuildDocument(docBson));
            }
        }

        return new TFoundStickers
        {
            Stickers = new TVector<IDocument>(stickers),
            Hash = 0,
            NextOffset = documentIds.Distinct().Count() > obj.Offset + stickers.Count ? obj.Offset + stickers.Count : null
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
