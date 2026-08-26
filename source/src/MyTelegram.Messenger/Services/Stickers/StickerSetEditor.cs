using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class StickerSetEditor(
    IMongoDatabase mongoDatabase,
    IStickerSetStore stickerSetStore,
    IStickerDocumentValidator documentValidator,
    IAppConfigHelper appConfigHelper) : IStickerSetEditor, ITransientDependency
{
    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#stickers-in-set-limit">how many stickers one set
    /// may hold</a>. Telegram does not publish it in appConfig, so the documented ceiling is the default.
    /// </summary>
    private const int StickersInSetLimitFallback = 120;

    private IMongoCollection<BsonDocument> Documents =>
        mongoDatabase.GetCollection<BsonDocument>(StickerSetMapper.DocumentCollectionName);

    public async Task<BsonDocument> CreateAsync(long ownerUserId, long stickerSetId, string title,
        string shortName, bool masks, bool emojis, bool textColor,
        IReadOnlyList<TInputStickerSetItem> stickers, long? thumbDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (stickers.Count == 0)
        {
            RpcErrors.RpcErrors400.StickersEmpty.ThrowRpcError();
        }

        if (stickers.Count > StickersInSetLimit)
        {
            RpcErrors.RpcErrors400.StickerpackStickersTooMuch.ThrowRpcError();
        }

        var setDocument = new BsonDocument
        {
            ["_id"] = $"stickersetreadmodel-{stickerSetId}",
            ["Id"] = $"stickersetreadmodel-{stickerSetId}",
            ["StickerSetId"] = stickerSetId,
            // The access hash a client sees is derived per session by AccessHashHelper2; this stored value
            // exists only so rows written here look like the seeded ones.
            ["AccessHash"] = stickerSetId,
            ["ShortName"] = shortName,
            ["Slug"] = shortName,
            ["Title"] = title,
            ["Count"] = 0,
            ["DocumentIds"] = new BsonArray(),
            ["Packs"] = new BsonArray(),
            ["Keywords"] = new BsonArray(),
            ["Masks"] = masks,
            ["Emojis"] = emojis,
            ["TextColor"] = textColor,
            ["Official"] = false,
            ["CreatorUserId"] = ownerUserId,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["Version"] = 0L
        };

        foreach (var sticker in stickers)
        {
            await ApplyAsync(setDocument, sticker, null, cancellationToken);
        }

        if (setDocument.GetInt64List("DocumentIds").Count == 0)
        {
            // Every sticker was rejected, so there is nothing to create. Silently inserting an empty set
            // used to leave the client with a pack it could never fill.
            RpcErrors.RpcErrors400.StickersEmpty.ThrowRpcError();
        }

        if (thumbDocumentId is > 0)
        {
            setDocument["ThumbDocumentId"] = thumbDocumentId.Value;
        }

        Touch(setDocument);
        await stickerSetStore.InsertAsync(setDocument, cancellationToken);

        return setDocument;
    }

    private int StickersInSetLimit =>
        appConfigHelper.GetInt32Value("stickers_in_set_limit", StickersInSetLimitFallback);

    public async Task AddAsync(BsonDocument stickerSetDocument, TInputStickerSetItem sticker,
        CancellationToken cancellationToken = default)
    {
        if (stickerSetDocument.GetInt64List("DocumentIds").Count >= StickersInSetLimit)
        {
            RpcErrors.RpcErrors400.StickerpackStickersTooMuch.ThrowRpcError();
        }

        if (!await ApplyAsync(stickerSetDocument, sticker, null, cancellationToken))
        {
            RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
        }

        await SaveAsync(stickerSetDocument, cancellationToken);
    }

    public async Task RemoveAsync(BsonDocument stickerSetDocument, long documentId,
        CancellationToken cancellationToken = default)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        if (!documentIds.Remove(documentId))
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var emojiByDocument = ReadEmojiByDocument(stickerSetDocument);
        var keywordsByDocument = ReadKeywordsByDocument(stickerSetDocument);
        emojiByDocument.Remove(documentId);
        keywordsByDocument.Remove(documentId);

        Rebuild(stickerSetDocument, documentIds, emojiByDocument, keywordsByDocument);
        await SaveAsync(stickerSetDocument, cancellationToken);
    }

    public async Task MoveAsync(BsonDocument stickerSetDocument, long documentId, int position,
        CancellationToken cancellationToken = default)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        if (!documentIds.Remove(documentId))
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        documentIds.Insert(Math.Clamp(position, 0, documentIds.Count), documentId);

        Rebuild(stickerSetDocument, documentIds, ReadEmojiByDocument(stickerSetDocument),
            ReadKeywordsByDocument(stickerSetDocument));
        await SaveAsync(stickerSetDocument, cancellationToken);
    }

    public async Task ReplaceAsync(BsonDocument stickerSetDocument, long oldDocumentId,
        TInputStickerSetItem newSticker, CancellationToken cancellationToken = default)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        var index = documentIds.IndexOf(oldDocumentId);
        if (index < 0)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        // The replacement inherits the position of the sticker it replaces, and its emoji and keywords when
        // the request does not carry new ones — that is what "replace" means to a client, which keeps the
        // grid layout it was showing.
        var emojiByDocument = ReadEmojiByDocument(stickerSetDocument);
        var keywordsByDocument = ReadKeywordsByDocument(stickerSetDocument);

        var inherited = new TInputStickerSetItem
        {
            Document = newSticker.Document,
            Emoji = string.IsNullOrEmpty(newSticker.Emoji)
                ? emojiByDocument.GetValueOrDefault(oldDocumentId, string.Empty)
                : newSticker.Emoji,
            MaskCoords = newSticker.MaskCoords,
            Keywords = string.IsNullOrEmpty(newSticker.Keywords)
                ? string.Join(',', keywordsByDocument.GetValueOrDefault(oldDocumentId, []))
                : newSticker.Keywords
        };

        documentIds.RemoveAt(index);
        emojiByDocument.Remove(oldDocumentId);
        keywordsByDocument.Remove(oldDocumentId);
        Rebuild(stickerSetDocument, documentIds, emojiByDocument, keywordsByDocument);

        if (!await ApplyAsync(stickerSetDocument, inherited, index, cancellationToken))
        {
            RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
        }

        await SaveAsync(stickerSetDocument, cancellationToken);
    }

    public async Task ChangeAsync(BsonDocument stickerSetDocument, long documentId, string? emoji,
        IMaskCoords? maskCoords, string? keywords, CancellationToken cancellationToken = default)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        if (!documentIds.Contains(documentId))
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var emojiByDocument = ReadEmojiByDocument(stickerSetDocument);
        var keywordsByDocument = ReadKeywordsByDocument(stickerSetDocument);

        if (!string.IsNullOrEmpty(emoji))
        {
            emojiByDocument[documentId] = emoji;
        }

        if (keywords != null)
        {
            var parsed = ParseKeywords(keywords);
            if (parsed.Count > 0)
            {
                keywordsByDocument[documentId] = parsed;
            }
            else
            {
                keywordsByDocument.Remove(documentId);
            }
        }

        Rebuild(stickerSetDocument, documentIds, emojiByDocument, keywordsByDocument);

        // The emoji and the mask coordinates live on the document too, because that is where every reader
        // outside a stickerset response takes them from.
        await WriteDocumentAttributeAsync(stickerSetDocument, documentId,
            emojiByDocument.GetValueOrDefault(documentId, string.Empty), maskCoords, cancellationToken);

        await SaveAsync(stickerSetDocument, cancellationToken);
    }

    public async Task SetThumbAsync(BsonDocument stickerSetDocument, long? thumbDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (thumbDocumentId is > 0)
        {
            stickerSetDocument["ThumbDocumentId"] = thumbDocumentId.Value;
        }
        else
        {
            stickerSetDocument.Remove("ThumbDocumentId");
        }

        await SaveAsync(stickerSetDocument, cancellationToken);
    }

    /// <summary>
    /// Validates the sticker, records its emoji and keywords, and writes its classification onto the
    /// document row. <paramref name="index"/> inserts at a position instead of appending.
    /// Returns false when the document is not usable as a sticker.
    /// </summary>
    private async Task<bool> ApplyAsync(BsonDocument stickerSetDocument, TInputStickerSetItem sticker,
        int? index, CancellationToken cancellationToken)
    {
        if (sticker.Document is not TInputDocument inputDocument)
        {
            return false;
        }

        if (!await documentValidator.IsStickerAsync(inputDocument.Id, true, cancellationToken))
        {
            return false;
        }

        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        var emojiByDocument = ReadEmojiByDocument(stickerSetDocument);
        var keywordsByDocument = ReadKeywordsByDocument(stickerSetDocument);

        // Adding a sticker already in the set moves it, rather than listing it twice: a duplicate id breaks
        // the pack grouping and every client's own index of the set.
        documentIds.Remove(inputDocument.Id);
        documentIds.Insert(index.HasValue ? Math.Clamp(index.Value, 0, documentIds.Count) : documentIds.Count,
            inputDocument.Id);

        emojiByDocument[inputDocument.Id] = sticker.Emoji ?? string.Empty;

        var parsedKeywords = ParseKeywords(sticker.Keywords);
        if (parsedKeywords.Count > 0)
        {
            keywordsByDocument[inputDocument.Id] = parsedKeywords;
        }

        Rebuild(stickerSetDocument, documentIds, emojiByDocument, keywordsByDocument);

        await WriteDocumentAttributeAsync(stickerSetDocument, inputDocument.Id, sticker.Emoji ?? string.Empty,
            sticker.MaskCoords, cancellationToken);

        return true;
    }

    /// <summary>
    /// Rewrites the four fields that describe the contents, keeping them consistent by construction.
    ///
    /// <para>Packs are grouped by emoji, not one per sticker: <c>stickerPack</c> is the set's emoji index,
    /// and clients build their own emoji-to-sticker map straight from it, so a pack per sticker means the
    /// same emoji appears repeatedly and only one of the stickers is ever found by it.</para>
    /// </summary>
    private static void Rebuild(BsonDocument stickerSetDocument, List<long> documentIds,
        Dictionary<long, string> emojiByDocument, Dictionary<long, List<string>> keywordsByDocument)
    {
        stickerSetDocument["DocumentIds"] = new BsonArray(documentIds);
        stickerSetDocument["Count"] = documentIds.Count;

        var packs = new List<(string Emoticon, List<long> Documents)>();
        var packByEmoticon = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (var documentId in documentIds)
        {
            var emoticon = emojiByDocument.GetValueOrDefault(documentId, string.Empty);
            if (emoticon.Length == 0)
            {
                continue;
            }

            if (!packByEmoticon.TryGetValue(emoticon, out var bucket))
            {
                bucket = [];
                packByEmoticon[emoticon] = bucket;
                packs.Add((emoticon, bucket));
            }

            bucket.Add(documentId);
        }

        stickerSetDocument["Packs"] = new BsonArray(packs.Select(p => new BsonDocument
        {
            ["Emoticon"] = p.Emoticon,
            ["Documents"] = new BsonArray(p.Documents)
        }));

        stickerSetDocument["Keywords"] = new BsonArray(documentIds
            .Where(keywordsByDocument.ContainsKey)
            .Select(p => new BsonDocument
            {
                ["DocumentId"] = p,
                ["Keyword"] = new BsonArray(keywordsByDocument[p])
            }));
    }

    private static Dictionary<long, string> ReadEmojiByDocument(BsonDocument stickerSetDocument)
    {
        return StickerSetPackReader.BuildAltByDocumentId(stickerSetDocument);
    }

    private static Dictionary<long, List<string>> ReadKeywordsByDocument(BsonDocument stickerSetDocument)
    {
        var result = new Dictionary<long, List<string>>();
        if (!stickerSetDocument.TryGetValue("Keywords", out var value) || !value.IsBsonArray)
        {
            return result;
        }

        foreach (var item in value.AsBsonArray.Where(p => p.IsBsonDocument))
        {
            var keyword = item.AsBsonDocument;
            if (!keyword.TryGetValue("Keyword", out var words) || !words.IsBsonArray)
            {
                continue;
            }

            result[keyword.GetInt64("DocumentId")] =
                words.AsBsonArray.Where(p => p.IsString).Select(p => p.AsString).ToList();
        }

        return result;
    }

    /// <summary>
    /// <c>inputStickerSetItem.keywords</c> is one comma-separated string, the same form the Bot API takes.
    /// </summary>
    private static List<string> ParseKeywords(string? keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            return [];
        }

        return keywords
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Writes the sticker classification onto the document row: which set it belongs to, its emoji, and —
    /// for a mask — where on a face it sits. Everything else the document carries (image size, video
    /// duration) survives untouched.
    /// </summary>
    private async Task WriteDocumentAttributeAsync(BsonDocument stickerSetDocument, long documentId,
        string alt, IMaskCoords? maskCoords, CancellationToken cancellationToken)
    {
        var row = await Documents
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", documentId))
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null)
        {
            return;
        }

        var setId = stickerSetDocument.GetInt64("StickerSetId");
        var stickerset = new TInputStickerSetID
        {
            Id = setId,
            AccessHash = stickerSetDocument.GetInt64("AccessHash")
        };

        var isEmojiSet = stickerSetDocument.GetBool("Emojis");
        IDocumentAttribute primary = isEmojiSet
            ? new TDocumentAttributeCustomEmoji
            {
                Alt = alt,
                Stickerset = stickerset,
                Free = true,
                TextColor = stickerSetDocument.GetBool("TextColor")
            }
            : new TDocumentAttributeSticker
            {
                Alt = alt,
                Stickerset = stickerset,
                Mask = stickerSetDocument.GetBool("Masks"),
                // Only meaningful for masks, and only when the client sent them; a mask with no coordinates
                // is placed on the forehead by default.
                MaskCoords = maskCoords
            };

        await Documents.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("DocumentId", documentId),
            Builders<BsonDocument>.Update.Set("Attributes2",
                StickerAttributeSerializer.WithPrimaryAttribute(row, primary)),
            cancellationToken: cancellationToken);
    }

    private Task SaveAsync(BsonDocument stickerSetDocument, CancellationToken cancellationToken)
    {
        Touch(stickerSetDocument);

        return stickerSetStore.ReplaceAsync(stickerSetDocument, cancellationToken);
    }

    /// <summary>
    /// Bumps the revision that feeds <see cref="StickerSetHashHelper"/>. Without it an edit that leaves the
    /// document ids and pack emoji alone — a new thumbnail, a keyword change — produces the same hash, and
    /// every client keeps the copy it already has.
    /// </summary>
    private static void Touch(BsonDocument stickerSetDocument)
    {
        stickerSetDocument["Version"] = stickerSetDocument.GetInt64("Version") + 1;
    }
}
