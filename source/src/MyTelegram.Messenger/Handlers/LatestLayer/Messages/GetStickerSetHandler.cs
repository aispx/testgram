using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stickers;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetStickerSetHandler(IMongoDatabase mongoDatabase, IAccessHashHelper2 accessHashHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetStickerSet, MyTelegram.Schema.Messages.IStickerSet>
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
            return await GetStickerSetBySlugAsync(input, DiceSlugMap.GetValueOrDefault(dice.Emoticon) ?? "", dice.Emoticon, obj.Hash);

        if (SpecialSetSlugMap.TryGetValue(obj.Stickerset.GetType(), out var slug))
            return await GetStickerSetBySlugAsync(input, slug, null, obj.Hash);

        if (obj.Stickerset is TInputStickerSetID setById)
            return await GetStickerSetByIdAsync(input, setById.Id, obj.Hash);

        if (obj.Stickerset is TInputStickerSetShortName shortNameSet)
            return await GetStickerSetBySlugAsync(input, shortNameSet.ShortName, null, obj.Hash);

        RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        return null!;
    }

    private async Task<MyTelegram.Schema.Messages.IStickerSet> GetStickerSetByIdAsync(IRequestInput input, long setId,
        int requestHash)
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

        return await BuildResponseAsync(input, setDoc, null, requestHash);
    }

    private async Task<MyTelegram.Schema.Messages.IStickerSet> GetStickerSetBySlugAsync(IRequestInput input,
        string slug, string? emoticon, int requestHash)
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

        return await BuildResponseAsync(input, setDoc, emoticon, requestHash);
    }

    private async Task<MyTelegram.Schema.Messages.IStickerSet> BuildResponseAsync(IRequestInput input,
        BsonDocument setDoc, string? emoticon, int requestHash)
    {
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var setId = GetInt64(setDoc["StickerSetId"]);
        var accessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, setId, AccessHashType.StickerSet);
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
                    AccessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, GetInt64(d["DocumentId"]), AccessHashType.Document),
                    FileReference = fileRef,
                    Date = GetInt32(d["Date"]),
                    MimeType = d["MimeType"].AsString,
                    Size = GetInt64(d["Size"]),
                    DcId = GetInt32(d["DcId"]),
                    Attributes = attributes,
                    Thumbs = ReadThumbs(d),
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

        // Computed over what is actually being returned, so a document row that appears or disappears
        // invalidates the client's copy. The alt is read back off the emitted attributes rather than
        // from the pack, so a corrected per-document alt also invalidates it. Deliberately excludes the
        // per-session access hashes and file references: those differ between sessions of the same
        // user, and a hash that moved with them could never match on the next poll.
        var hash = StickerSetHashHelper.ComputeHash(setId, shortName, count,
            documents.Cast<TDocument>().Select(document => (document.Id, ReadAlt(document))));

        // A zero request hash means the client has nothing cached, so it can never be satisfied by
        // notModified even if our hash happened to be zero — which it never is.
        if (requestHash != 0 && requestHash == hash)
        {
            return new TStickerSetNotModified();
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
                Hash = hash,
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
        else
        {
            foreach (var attribute in compatibleAttributes)
            {
                switch (attribute)
                {
                    case TDocumentAttributeCustomEmoji customEmoji:
                        customEmoji.Alt = PreferStoredAlt(customEmoji.Alt, alt);
                        customEmoji.Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash };
                        customEmoji.TextColor = textColor;
                        break;
                    case TDocumentAttributeSticker sticker:
                        sticker.Alt = PreferStoredAlt(sticker.Alt, alt);
                        sticker.Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash };
                        break;
                }
            }
        }

        return new TVector<IDocumentAttribute>(compatibleAttributes);
    }

    /// <summary>
    /// The alt recorded on the document wins over the one derived from the pack it belongs to.
    ///
    /// <para>Telegram's own <c>stickerPack.emoticon</c> carries no U+FE0F variation selector while the
    /// documents' <c>alt</c> does, for 207 of the seeded documents. Deriving alt from the pack therefore
    /// silently stripped it — harmless on Android, which strips FE0F on both sides of the comparison in
    /// <c>MediaDataController.getEmojiAnimatedSticker</c>, but tdlib-based clients (iOS, Desktop, tdweb)
    /// compare the raw string and then fail to find the emoji they asked for.</para>
    ///
    /// <para>The pack emoticon remains the fallback: plain sticker sets seeded without a per-document
    /// alt have nothing else to offer, and an empty alt is what makes a sticker unsearchable.</para>
    /// </summary>
    private static string PreferStoredAlt(string? storedAlt, string altFromPack)
    {
        return string.IsNullOrEmpty(storedAlt) ? altFromPack : storedAlt;
    }

    /// <summary>The alt actually being sent, whichever attribute kind carries it.</summary>
    private static string? ReadAlt(TDocument document)
    {
        foreach (var attribute in document.Attributes ?? [])
        {
            switch (attribute)
            {
                case TDocumentAttributeCustomEmoji customEmoji:
                    return customEmoji.Alt;
                case TDocumentAttributeSticker sticker:
                    return sticker.Alt;
            }
        }

        return null;
    }

    private static TVector<IPhotoSize> ReadThumbs(BsonDocument document)
    {
        var result = new TVector<IPhotoSize>();
        if (!document.TryGetValue("Thumbs", out var thumbsValue) || !thumbsValue.IsBsonArray)
        {
            return result;
        }

        foreach (var value in thumbsValue.AsBsonArray.Where(value => value.IsBsonDocument))
        {
            var thumb = value.AsBsonDocument;
            var type = thumb.GetValue("_t", "").AsString;
            var thumbType = thumb.GetValue("Type", "").AsString;

            switch (type)
            {
                case nameof(TPhotoSize):
                    result.Add(new TPhotoSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Size = GetInt32(thumb["Size"]),
                    });
                    break;
                case nameof(TPhotoCachedSize):
                    result.Add(new TPhotoCachedSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Bytes = GetBytes(thumb["Bytes"]),
                    });
                    break;
                case nameof(TPhotoSizeProgressive):
                    result.Add(new TPhotoSizeProgressive
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Sizes = new TVector<int>(thumb["Sizes"].AsBsonArray.Select(GetInt32)),
                    });
                    break;
                case nameof(TPhotoStrippedSize):
                    result.Add(new TPhotoStrippedSize { Type = thumbType, Bytes = GetBytes(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoPathSize):
                    result.Add(new TPhotoPathSize { Type = thumbType, Bytes = GetBytes(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoSizeEmpty):
                    result.Add(new TPhotoSizeEmpty { Type = thumbType });
                    break;
            }
        }

        return result;
    }

    private static byte[] GetBytes(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Binary => value.AsBsonBinaryData.Bytes,
            BsonType.Array => value.AsBsonArray.Select(item => (byte)GetInt32(item)).ToArray(),
            _ => [],
        };
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
