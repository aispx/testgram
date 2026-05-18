using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Gets featured custom emoji stickersets.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getFeaturedEmojiStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetFeaturedEmojiStickersHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetFeaturedEmojiStickers, MyTelegram.Schema.Messages.IFeaturedStickers>
{
    protected override async Task<MyTelegram.Schema.Messages.IFeaturedStickers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetFeaturedEmojiStickers obj)
    {
        var featuredCol = mongoDatabase.GetCollection<BsonDocument>("featured_emoji_sticker_sets");
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var featuredDocs = await featuredCol.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("Order").Ascending("StickerSetId"))
            .ToListAsync();
        var hash = featuredDocs.Count == 0 ? 0L : featuredDocs.Max(x => x.Contains("Version") ? x["Version"].ToInt64() : 0L);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TFeaturedStickersNotModified { Count = featuredDocs.Count };
        }

        var setIds = featuredDocs.Where(x => x.Contains("StickerSetId")).Select(x => x["StickerSetId"].ToInt64()).Distinct().ToList();
        var setDocs = await setCol.Find(Builders<BsonDocument>.Filter.In("StickerSetId", setIds.Select(x => (BsonValue)new BsonInt64(x)))).ToListAsync();
        var setMap = setDocs.ToDictionary(x => x["StickerSetId"].ToInt64());
        var allDocumentIds = setDocs
            .Where(x => x.Contains("DocumentIds") && x["DocumentIds"].IsBsonArray)
            .SelectMany(x => x["DocumentIds"].AsBsonArray.Select(y => y.ToInt64()))
            .Distinct()
            .ToList();
        var documentDocs = await docCol.Find(Builders<BsonDocument>.Filter.In("DocumentId", allDocumentIds.Select(x => (BsonValue)new BsonInt64(x)))).ToListAsync();
        var documentMap = documentDocs.ToDictionary(x => x["DocumentId"].ToInt64());

        var sets = new List<IStickerSetCovered>();
        var unread = new List<long>();
        foreach (var featuredDoc in featuredDocs)
        {
            var setId = featuredDoc.Contains("StickerSetId") ? featuredDoc["StickerSetId"].ToInt64() : 0;
            if (!setMap.TryGetValue(setId, out var setDoc))
            {
                continue;
            }

            var stickerSet = BuildStickerSet(setDoc);
            var packs = BuildPacks(setDoc);
            var keywords = BuildKeywords(setDoc);
            var documents = BuildDocuments(setDoc, documentMap, packs);
            sets.Add(new TStickerSetFullCovered
            {
                Set = stickerSet,
                Packs = new TVector<IStickerPack>(packs),
                Keywords = new TVector<IStickerKeyword>(keywords),
                Documents = new TVector<IDocument>(documents)
            });

            if (featuredDoc.Contains("Unread") && featuredDoc["Unread"].ToBoolean())
            {
                unread.Add(setId);
            }
        }

        return new TFeaturedStickers
        {
            Hash = hash,
            Count = sets.Count,
            Sets = new TVector<IStickerSetCovered>(sets),
            Unread = new TVector<long>(unread)
        };
    }

    private static Schema.TStickerSet BuildStickerSet(BsonDocument setDoc)
    {
        return new Schema.TStickerSet
        {
            Id = setDoc["StickerSetId"].ToInt64(),
            AccessHash = setDoc.Contains("AccessHash") ? setDoc["AccessHash"].ToInt64() : 0,
            Title = setDoc.Contains("Title") ? setDoc["Title"].AsString : string.Empty,
            ShortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : string.Empty,
            Count = setDoc.Contains("Count") ? setDoc["Count"].ToInt32() : 0,
            Hash = 0,
            Emojis = setDoc.Contains("Emojis") && setDoc["Emojis"].ToBoolean(),
            TextColor = setDoc.Contains("TextColor") && setDoc["TextColor"].ToBoolean()
        };
    }

    private static List<IStickerPack> BuildPacks(BsonDocument setDoc)
    {
        if (!setDoc.Contains("Packs") || !setDoc["Packs"].IsBsonArray)
        {
            return [];
        }

        return setDoc["Packs"].AsBsonArray.Select(x => (IStickerPack)new TStickerPack
        {
            Emoticon = x["Emoticon"].AsString,
            Documents = new TVector<long>(x["Documents"].AsBsonArray.Select(y => y.ToInt64()).ToList())
        }).ToList();
    }

    private static List<IStickerKeyword> BuildKeywords(BsonDocument setDoc)
    {
        if (!setDoc.Contains("Keywords") || !setDoc["Keywords"].IsBsonArray)
        {
            return [];
        }

        return setDoc["Keywords"].AsBsonArray.Select(x => (IStickerKeyword)new TStickerKeyword
        {
            DocumentId = x["DocumentId"].ToInt64(),
            Keyword = new TVector<string>(x["Keyword"].AsBsonArray.Select(y => y.AsString).ToList())
        }).ToList();
    }

    private static List<IDocument> BuildDocuments(BsonDocument setDoc, Dictionary<long, BsonDocument> documentMap, List<IStickerPack> packs)
    {
        if (!setDoc.Contains("DocumentIds") || !setDoc["DocumentIds"].IsBsonArray)
        {
            return [];
        }

        var result = new List<IDocument>();
        foreach (var documentIdValue in setDoc["DocumentIds"].AsBsonArray)
        {
            try
            {
                var documentId = documentIdValue.ToInt64();
                if (!documentMap.TryGetValue(documentId, out var doc))
                {
                    continue;
                }

                result.Add(BuildDocument(doc, setDoc, documentId, packs));
            }
            catch
            {
                // Skip malformed document entries
            }
        }

        return result;
    }

    private static string GetAltForDocument(long documentId, List<IStickerPack> packs)
    {
        foreach (var pack in packs)
        {
            if (pack.Documents.Contains(documentId))
            {
                return pack.Emoticon;
            }
        }
        return string.Empty;
    }

    private static IDocument BuildDocument(BsonDocument d, BsonDocument setDoc, long documentId, List<IStickerPack> packs)
    {
        byte[] fileRef = [];
        if (d.Contains("FileReference") && !d["FileReference"].IsBsonNull)
        {
            var fr = d["FileReference"];
            if (fr.BsonType == BsonType.Binary)
                fileRef = fr.AsBsonBinaryData.Bytes;
            else if (fr.BsonType == BsonType.Array)
                fileRef = fr.AsBsonArray.Select(x => (byte)x.ToInt32()).ToArray();
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

        if (attributes.Count == 0)
        {
            var setId = setDoc["StickerSetId"].ToInt64();
            var accessHash = setDoc.Contains("AccessHash") ? setDoc["AccessHash"].ToInt64() : 0;
            var alt = GetAltForDocument(documentId, packs);
            var isEmoji = setDoc.Contains("Emojis") && setDoc["Emojis"].ToBoolean();

            var fallbackAttributes = new List<IDocumentAttribute>
            {
                new TDocumentAttributeSticker
                {
                    Alt = alt,
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                    Mask = false,
                }
            };

            if (isEmoji)
            {
                fallbackAttributes.Add(new TDocumentAttributeCustomEmoji
                {
                    Alt = alt,
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                    Free = true,
                });
            }

            attributes = new TVector<IDocumentAttribute>(fallbackAttributes);
        }

        return new TDocument
        {
            Id = d["DocumentId"].ToInt64(),
            AccessHash = d.Contains("AccessHash") ? d["AccessHash"].ToInt64() : 0,
            FileReference = fileRef,
            Date = d.Contains("Date") ? d["Date"].ToInt32() : 0,
            MimeType = d.Contains("MimeType") ? d["MimeType"].AsString : "application/octet-stream",
            Size = d.Contains("Size") ? d["Size"].ToInt64() : 0,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = d.Contains("DcId") ? d["DcId"].ToInt32() : 0,
            Attributes = attributes
        };
    }
}