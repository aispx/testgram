using MongoDB.Bson;
using MongoDB.Driver;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetStickerSetHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetStickerSet, MyTelegram.Schema.Messages.IStickerSet>
{
    private static readonly Dictionary<string, string> DiceSlugMap = new()
    {
        ["🎲"] = "AnimatedDice2",
        ["🎯"] = "AnimatedDart",
        ["🏀"] = "AnimatedBasketball",
        ["⚽"] = "AnimatedPenalty",
        ["⚽️"] = "AnimatedPenalty",
        ["🎰"] = "SlotMachineAnimated",
        ["🎳"] = "AnimatedBowling",
    };

    private static readonly Dictionary<Type, string> SpecialSetSlugMap = new()
    {
        [typeof(TInputStickerSetAnimatedEmoji)] = "AnimatedEmojies",
        [typeof(TInputStickerSetAnimatedEmojiAnimations)] = "EmojiAnimations",
        [typeof(TInputStickerSetPremiumGifts)] = "GiftsPremium",
        [typeof(TInputStickerSetEmojiGenericAnimations)] = "EmojiGenericAnimations",
        [typeof(TInputStickerSetEmojiDefaultStatuses)] = "StatusPack",
        [typeof(TInputStickerSetEmojiDefaultTopicIcons)] = "Topics",
        [typeof(TInputStickerSetEmojiChannelDefaultStatuses)] = "StatusPack",
        [typeof(TInputStickerSetTonGifts)] = "GiftsTons",
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
        var isEmojiSet = setDoc.Contains("Emojis") && setDoc["Emojis"].ToBoolean();
        var textColor = setDoc.Contains("TextColor") && setDoc["TextColor"].ToBoolean();

        var docIds = GetInt64List(setDoc["DocumentIds"].AsBsonArray);

        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", docIds.Select(id => (BsonValue)new BsonInt64(id)));
        var docDocs = await docCol.Find(docFilter).ToListAsync();
        var altByDocumentId = BuildAltByDocumentId(setDoc, emoticon);

        var docMap = docDocs.ToDictionary(d => GetInt64(d["DocumentId"]));
        var documents = docIds
            .Where(id => docMap.ContainsKey(id))
            .Select(id =>
            {
                var d = docMap[id];
                var alt = altByDocumentId.GetValueOrDefault(id) ?? string.Empty;

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

                // Use Attributes2 if available, otherwise create the correct fallback attribute
                // for the set kind so clients do not confuse stickers with custom emoji.
                TVector<IDocumentAttribute> attributes;
                if (d.Contains("Attributes2") && !d["Attributes2"].IsBsonNull)
                {
                    try
                    {
                        attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(d["Attributes2"].ToJson());
                        attributes = NormalizeAttributesForSetKind(attributes, isEmojiSet, textColor, alt, setId, accessHash);
                    }
                    catch
                    {
                        attributes = BuildFallbackAttributes(isEmojiSet, textColor, alt, setId, accessHash);
                    }
                }
                else
                {
                    attributes = BuildFallbackAttributes(isEmojiSet, textColor, alt, setId, accessHash);
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
                Emojis = isEmojiSet,
                TextColor = textColor,
            }
        };
    }

    private static TVector<IDocumentAttribute> BuildFallbackAttributes(bool isEmojiSet, bool textColor, string alt, long setId, long accessHash)
    {
        return isEmojiSet
            ?
            [
                new TDocumentAttributeCustomEmoji
                {
                    Alt = alt,
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                    Free = true,
                    TextColor = textColor,
                }
            ]
            :
            [
                new TDocumentAttributeSticker
                {
                    Alt = alt,
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                    Mask = false,
                }
            ];
    }

    private static TVector<IDocumentAttribute> NormalizeAttributesForSetKind(
        TVector<IDocumentAttribute> attributes,
        bool isEmojiSet,
        bool textColor,
        string alt,
        long setId,
        long accessHash)
    {
        var compatibleAttributes = attributes
            .Where(attribute => isEmojiSet
                ? attribute is not TDocumentAttributeSticker
                : attribute is not TDocumentAttributeCustomEmoji)
            .ToList();

        var hasExpectedPrimaryAttribute = isEmojiSet
            ? compatibleAttributes.Any(attribute => attribute is TDocumentAttributeCustomEmoji)
            : compatibleAttributes.Any(attribute => attribute is TDocumentAttributeSticker);

        if (!hasExpectedPrimaryAttribute)
        {
            compatibleAttributes.InsertRange(0, BuildFallbackAttributes(isEmojiSet, textColor, alt, setId, accessHash));
        }

        return new TVector<IDocumentAttribute>(compatibleAttributes);
    }

    private static Dictionary<long, string> BuildAltByDocumentId(BsonDocument setDoc, string? fallbackEmoticon)
    {
        var result = new Dictionary<long, string>();
        if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
        {
            foreach (var packValue in setDoc["Packs"].AsBsonArray)
            {
                if (!packValue.IsBsonDocument)
                {
                    continue;
                }

                var pack = packValue.AsBsonDocument;
                var emoticon = pack.Contains("Emoticon") && pack["Emoticon"].IsString
                    ? pack["Emoticon"].AsString
                    : string.Empty;
                if (!pack.Contains("Documents") || !pack["Documents"].IsBsonArray)
                {
                    continue;
                }

                foreach (var value in pack["Documents"].AsBsonArray)
                {
                    var documentId = GetInt64(value);
                    if (!result.ContainsKey(documentId))
                    {
                        result[documentId] = emoticon;
                    }
                }
            }
        }

        if (fallbackEmoticon != null)
        {
            foreach (var documentId in GetInt64List(setDoc["DocumentIds"].AsBsonArray))
            {
                result.TryAdd(documentId, fallbackEmoticon);
            }
        }

        return result;
    }
}
