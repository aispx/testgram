using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Search for <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickersets »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchEmojiStickerSets"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchEmojiStickerSetsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearchEmojiStickerSets, MyTelegram.Schema.Messages.IFoundStickerSets>
{
    protected override async Task<MyTelegram.Schema.Messages.IFoundStickerSets> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSearchEmojiStickerSets obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var query = obj.Q?.Trim().ToLower() ?? "";

        if (string.IsNullOrEmpty(query))
        {
            return new MyTelegram.Schema.Messages.TFoundStickerSets
            {
                Hash = 0,
                Sets = new TVector<IStickerSetCovered>()
            };
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Emojis", true),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Regex("Title", new BsonRegularExpression(query, "i")),
                Builders<BsonDocument>.Filter.Regex("ShortName", new BsonRegularExpression(query, "i"))
            )
        );

        var sets = await setCol.Find(filter).Limit(20).ToListAsync();

        var results = new List<IStickerSetCovered>();

        foreach (var setDoc in sets)
        {
            var setId = GetInt64(setDoc["StickerSetId"]);
            var accessHash = GetInt64(setDoc["AccessHash"]);
            var title = setDoc["Title"].AsString;
            var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;
            var count = GetInt32(setDoc["Count"]);

            var docIds = GetInt64List(setDoc["DocumentIds"].AsBsonArray);
            var firstDocId = docIds.FirstOrDefault();

            if (firstDocId == 0)
                continue;

            var docDoc = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", firstDocId)).FirstOrDefaultAsync();

            if (docDoc == null)
                continue;

            byte[] fileRef;
            if (docDoc.Contains("FileReference") && !docDoc["FileReference"].IsBsonNull)
            {
                var fr = docDoc["FileReference"];
                if (fr.BsonType == BsonType.Binary)
                    fileRef = fr.AsBsonBinaryData.Bytes;
                else if (fr.BsonType == BsonType.Array)
                    fileRef = fr.AsBsonArray.Select(b => (byte)GetInt32(b)).ToArray();
                else
                    fileRef = [];
            }
            else
            {
                fileRef = [];
            }

            TVector<IDocumentAttribute> attributes;
            if (docDoc.Contains("Attributes2") && !docDoc["Attributes2"].IsBsonNull)
            {
                try
                {
                    attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(docDoc["Attributes2"].ToJson());
                }
                catch
                {
                    attributes = [new TDocumentAttributeSticker
                    {
                        Alt = "",
                        Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                        Mask = false,
                    }];
                }
            }
            else
            {
                attributes = [new TDocumentAttributeSticker
                {
                    Alt = "",
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                    Mask = false,
                }];
            }

            var coverDoc = new TDocument
            {
                Id = GetInt64(docDoc["DocumentId"]),
                AccessHash = GetInt64(docDoc["AccessHash"]),
                FileReference = fileRef,
                Date = GetInt32(docDoc["Date"]),
                MimeType = docDoc["MimeType"].AsString,
                Size = GetInt64(docDoc["Size"]),
                DcId = GetInt32(docDoc["DcId"]),
                Attributes = attributes,
                Thumbs = new TVector<IPhotoSize>(),
                VideoThumbs = new TVector<IVideoSize>(),
            };

            results.Add(new TStickerSetCovered
            {
                Set = new Schema.TStickerSet
                {
                    Id = setId,
                    AccessHash = accessHash,
                    Title = title,
                    ShortName = shortName,
                    Count = count,
                    Hash = 0,
                    Emojis = true
                },
                Cover = coverDoc
            });
        }

        var resultHash = results.Count > 0 ? results.Select(s => ((TStickerSetCovered)s).Set.Id).Aggregate((a, b) => a ^ b) : 0;

        return new MyTelegram.Schema.Messages.TFoundStickerSets
        {
            Hash = resultHash,
            Sets = new TVector<IStickerSetCovered>(results)
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

    private static List<long> GetInt64List(BsonArray arr)
    {
        return arr.Select(x => GetInt64(x)).ToList();
    }
}