using MongoDB.Bson;
using MongoDB.Driver;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetStickerSetHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetStickerSet, MyTelegram.Schema.Messages.IStickerSet>
{
    private static readonly Dictionary<string, string> DiceSlugMap = new()
    {
        ["🎲"] = "dice_🎲",
        ["🎯"] = "dice_🎯",
        ["🏀"] = "dice_🏀",
        ["⚽"] = "dice_⚽",
        ["⚽️"] = "dice_⚽",
        ["🎰"] = "dice_🎰",
        ["🎳"] = "dice_🎳",
    };

    private static readonly Dictionary<Type, string> SpecialSetSlugMap = new()
    {
        [typeof(TInputStickerSetAnimatedEmoji)] = "animated_emoji",
        [typeof(TInputStickerSetAnimatedEmojiAnimations)] = "animated_emoji_animations",
        [typeof(TInputStickerSetPremiumGifts)] = "premium_gifts",
        [typeof(TInputStickerSetEmojiGenericAnimations)] = "emoji_generic_animations",
        [typeof(TInputStickerSetEmojiDefaultStatuses)] = "emoji_default_statuses",
        [typeof(TInputStickerSetEmojiDefaultTopicIcons)] = "emoji_default_topic_icons",
        [typeof(TInputStickerSetEmojiChannelDefaultStatuses)] = "emoji_channel_statuses",
        [typeof(TInputStickerSetTonGifts)] = "ton_gifts",
    };

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

    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetStickerSet obj)
    {
        if (obj.Stickerset is TInputStickerSetDice dice)
            return await GetStickerSetBySlugAsync(DiceSlugMap.GetValueOrDefault(dice.Emoticon) ?? "", dice.Emoticon);

        if (SpecialSetSlugMap.TryGetValue(obj.Stickerset.GetType(), out var slug))
            return await GetStickerSetBySlugAsync(slug, null);

        if (obj.Stickerset is TInputStickerSetID setById)
            return await GetStickerSetByIdAsync(setById.Id);

        if (obj.Stickerset is TInputStickerSetShortName shortNameSet)
            return await GetStickerSetBySlugAsync(shortNameSet.ShortName, null);

        RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        return null!;
    }

    private async Task<MyTelegram.Schema.Messages.IStickerSet> GetStickerSetByIdAsync(long setId)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", setId)).FirstOrDefaultAsync();
        if (setDoc == null)
        {
            return new TStickerSet
            {
                Packs = new TVector<IStickerPack>(),
                Documents = new TVector<IDocument>(),
                Keywords = new TVector<IStickerKeyword>(),
                Set = new Schema.TStickerSet { Id = setId, AccessHash = 0, Title = "", ShortName = "", Count = 0, Hash = 0 }
            };
        }

        return await BuildResponseAsync(setDoc, null);
    }

    private async Task<MyTelegram.Schema.Messages.IStickerSet> GetStickerSetBySlugAsync(string slug, string? emoticon)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return new TStickerSet
            {
                Packs = new TVector<IStickerPack>(),
                Documents = new TVector<IDocument>(),
                Keywords = new TVector<IStickerKeyword>(),
                Set = new Schema.TStickerSet { Id = 0, AccessHash = 0, Title = "", ShortName = "", Count = 0, Hash = 0 }
            };
        }

        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        
        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("Slug", slug)).FirstOrDefaultAsync();
        
        if (setDoc == null)
        {
            setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("ShortName", slug)).FirstOrDefaultAsync();
        }
        
        if (setDoc == null)
        {
            Console.WriteLine($"[WARN] StickerSet not found in DB: {slug}");
            return new TStickerSet
            {
                Packs = new TVector<IStickerPack>(),
                Documents = new TVector<IDocument>(),
                Keywords = new TVector<IStickerKeyword>(),
                Set = new Schema.TStickerSet { Id = 0, AccessHash = 0, Title = "", ShortName = slug, Count = 0, Hash = 0 }
            };
        }

        return await BuildResponseAsync(setDoc, emoticon);
    }

    private async Task<MyTelegram.Schema.Messages.IStickerSet> BuildResponseAsync(BsonDocument setDoc, string? emoticon)
    {
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var setId = GetInt64(setDoc["StickerSetId"]);
        var accessHash = GetInt64(setDoc["AccessHash"]);
        var title = setDoc["Title"].AsString;
        var shortName = setDoc["ShortName"].AsString;
        var count = GetInt32(setDoc["Count"]);

        var docIds = GetInt64List(setDoc["DocumentIds"].AsBsonArray);
        Console.WriteLine($"[DEBUG] BuildResponse: slug={shortName}, docIds.Count={docIds.Count}");

        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", docIds.Select(id => (BsonValue)new BsonInt64(id)));
        var docDocs = await docCol.Find(docFilter).ToListAsync();
        Console.WriteLine($"[DEBUG] Found docs in DB: {docDocs.Count}");

        var docMap = docDocs.ToDictionary(d => GetInt64(d["DocumentId"]));
        var documents = docIds
            .Where(id => docMap.ContainsKey(id))
            .Select(id =>
            {
                var d = docMap[id];
                var alt = emoticon ?? string.Empty;

                // Handle FileReference safely
                byte[] fileRef;
                if (d.Contains("FileReference") && !d["FileReference"].IsBsonNull)
                {
                    var fr = d["FileReference"];
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

                // Use Attributes2 if available, otherwise create default sticker attribute
                TVector<IDocumentAttribute> attributes;
                if (d.Contains("Attributes2") && !d["Attributes2"].IsBsonNull)
                {
                    try
                    {
                        attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(d["Attributes2"].ToJson());
                    }
                    catch
                    {
                        // Fallback to default attribute
                        attributes = [new TDocumentAttributeSticker
                        {
                            Alt = alt,
                            Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                            Mask = false,
                        }];
                    }
                }
                else
                {
                    attributes = [new TDocumentAttributeSticker
                    {
                        Alt = alt,
                        Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                        Mask = false,
                    }];
                }

                return (IDocument)new TDocument
                {
                    Id = GetInt64(d["DocumentId"]),
                    AccessHash = GetInt64(d["AccessHash"]),
                    FileReference = fileRef,
                    Date = GetInt32(d["Date"]),
                    MimeType = d["MimeType"].AsString,
                    Size = GetInt64(d["Size"]),
                    DcId = GetInt32(d["DcId"]),
                    Attributes = attributes,
                    Thumbs = new TVector<IPhotoSize>(),
                    VideoThumbs = new TVector<IVideoSize>(),
                };
            }).ToList();

        var packs = new List<IStickerPack>();
        if (setDoc.Contains("Packs") && !setDoc["Packs"].IsBsonNull)
        {
            foreach (var p in setDoc["Packs"].AsBsonArray)
            {
                packs.Add(new TStickerPack
                {
                    Emoticon = p["Emoticon"].AsString,
                    Documents = new TVector<long>(GetInt64List(p["Documents"].AsBsonArray)),
                });
            }
        }
        else if (emoticon != null)
        {
            packs.Add(new TStickerPack { Emoticon = emoticon, Documents = new TVector<long>(docIds) });
        }

        var keywords = new List<IStickerKeyword>();
        if (setDoc.Contains("Keywords") && !setDoc["Keywords"].IsBsonNull && setDoc["Keywords"].IsBsonArray)
        {
            foreach (var keyword in setDoc["Keywords"].AsBsonArray)
            {
                keywords.Add(new TStickerKeyword
                {
                    DocumentId = GetInt64(keyword["DocumentId"]),
                    Keyword = new TVector<string>(keyword["Keyword"].AsBsonArray.Select(x => x.AsString).ToList())
                });
            }
        }

        return new TStickerSet
        {
            Packs = new TVector<IStickerPack>(packs),
            Documents = new TVector<IDocument>(documents),
            Keywords = new TVector<IStickerKeyword>(keywords),
            Set = new Schema.TStickerSet
            {
                Id = setId,
                AccessHash = accessHash,
                Title = title,
                ShortName = shortName,
                Count = count,
                Hash = 0,
            }
        };
    }
}
