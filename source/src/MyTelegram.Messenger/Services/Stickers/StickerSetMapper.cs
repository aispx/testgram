using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class StickerSetMapper(
    IMongoDatabase mongoDatabase,
    IStickerSetStore stickerSetStore,
    IInstalledStickerSetStore installedStickerSetStore,
    IAccessHashHelper2 accessHashHelper,
    IFileReferenceHelper fileReferenceHelper) : IStickerSetMapper, ITransientDependency
{
    public const string DocumentCollectionName = "eventflow-documentreadmodel";

    private IMongoCollection<BsonDocument> Documents =>
        mongoDatabase.GetCollection<BsonDocument>(DocumentCollectionName);

    public Schema.TStickerSet BuildHeader(IRequestInput input, BsonDocument stickerSetDocument,
        InstalledStickerSetDocument? installed)
    {
        var setId = stickerSetDocument.GetInt64("StickerSetId");
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        var thumbs = StickerSetPackReader.ReadThumbs(stickerSetDocument);
        var thumbDocumentId = stickerSetDocument.GetInt64("ThumbDocumentId");

        return new Schema.TStickerSet
        {
            Id = setId,
            AccessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, setId,
                AccessHashType.StickerSet),
            Title = stickerSetDocument.GetString("Title"),
            ShortName = StickerSetPackReader.ReadShortName(stickerSetDocument),
            Count = stickerSetDocument.GetInt32("Count", documentIds.Count),
            Hash = StickerSetHashHelper.ComputeHash(stickerSetDocument),
            Masks = stickerSetDocument.GetBool("Masks"),
            Emojis = stickerSetDocument.GetBool("Emojis"),
            TextColor = stickerSetDocument.GetBool("TextColor"),
            ChannelEmojiStatus = stickerSetDocument.GetBool("ChannelEmojiStatus"),
            Official = stickerSetDocument.GetBool("Official"),
            // creator is per-user: it gates the "edit this pack" UI, so it must reflect who is asking.
            Creator = stickerSetDocument.GetInt64("CreatorUserId") == input.UserId,
            Archived = installed?.Archived ?? false,
            // installed_date doubles as the "is it installed" flag — the thumbs/thumb_version group and
            // this one are the only optional fields clients branch on structurally.
            InstalledDate = installed?.Date,
            Thumbs = thumbs.Count > 0 ? thumbs : null,
            ThumbDcId = thumbs.Count > 0 ? stickerSetDocument.GetInt32("ThumbDcId", MyTelegramConsts.MediaDcId) : null,
            ThumbVersion = thumbs.Count > 0 ? stickerSetDocument.GetInt32("ThumbVersion") : null,
            ThumbDocumentId = thumbDocumentId != 0 ? thumbDocumentId : null
        };
    }

    public async Task<Schema.Messages.TStickerSet> BuildFullAsync(IRequestInput input,
        BsonDocument stickerSetDocument, string? diceEmoticon = null,
        CancellationToken cancellationToken = default)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        var documents = await BuildSetDocumentsAsync(input, stickerSetDocument, documentIds, diceEmoticon,
            cancellationToken);

        var installed = await GetInstalledAsync(input.UserId, stickerSetDocument.GetInt64("StickerSetId"),
            cancellationToken);

        return new Schema.Messages.TStickerSet
        {
            Set = BuildHeader(input, stickerSetDocument, installed),
            Packs = new TVector<IStickerPack>(StickerSetPackReader.ReadPacks(stickerSetDocument, diceEmoticon)),
            Keywords = new TVector<IStickerKeyword>(StickerSetPackReader.ReadKeywords(stickerSetDocument)),
            Documents = new TVector<IDocument>(documents)
        };
    }

    public async Task<Schema.Messages.TStickerSet?> BuildFullByIdAsync(IRequestInput input, long stickerSetId,
        CancellationToken cancellationToken = default)
    {
        var stickerSetDocument = await stickerSetStore.FindByIdAsync(stickerSetId, cancellationToken);

        return stickerSetDocument == null
            ? null
            : await BuildFullAsync(input, stickerSetDocument, cancellationToken: cancellationToken);
    }

    public async Task<List<IStickerSetCovered>> BuildCoveredAsync(IRequestInput input,
        IReadOnlyList<BsonDocument> stickerSetDocuments, bool full,
        CancellationToken cancellationToken = default)
    {
        if (stickerSetDocuments.Count == 0)
        {
            return [];
        }

        var setIds = stickerSetDocuments.Select(p => p.GetInt64("StickerSetId")).ToList();
        var overlay = await installedStickerSetStore.GetOverlayAsync(input.UserId, setIds, cancellationToken);

        // One query for every document of every set: a cover is one document per set, but the full form
        // needs all of them, and either way N+1 is what made the trending page slow.
        var wantedIds = stickerSetDocuments
            .SelectMany(p => full ? p.GetInt64List("DocumentIds") : Cover(p))
            .Distinct()
            .ToList();
        var documentRows = await LoadDocumentRowsAsync(wantedIds, cancellationToken);

        var result = new List<IStickerSetCovered>(stickerSetDocuments.Count);
        foreach (var setDocument in stickerSetDocuments)
        {
            var setId = setDocument.GetInt64("StickerSetId");
            var header = BuildHeader(input, setDocument, overlay.GetValueOrDefault(setId));
            var documentIds = full ? setDocument.GetInt64List("DocumentIds") : Cover(setDocument);
            var documents = BuildDocuments(input, setDocument, documentIds, documentRows, null);

            if (full)
            {
                result.Add(new TStickerSetFullCovered
                {
                    Set = header,
                    Packs = new TVector<IStickerPack>(StickerSetPackReader.ReadPacks(setDocument)),
                    Keywords = new TVector<IStickerKeyword>(StickerSetPackReader.ReadKeywords(setDocument)),
                    Documents = new TVector<IDocument>(documents)
                });

                continue;
            }

            // stickerSetCovered.cover is not optional, so a set whose documents are all missing still has
            // to carry something; documentEmpty is what the official server sends and clients skip it.
            result.Add(new TStickerSetCovered
            {
                Set = header,
                Cover = documents.Count > 0 ? documents[0] : new TDocumentEmpty { Id = 0 }
            });
        }

        return result;
    }

    /// <summary>The first document of a set, which is the preview clients draw on the trending page.</summary>
    private static List<long> Cover(BsonDocument stickerSetDocument)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");

        return documentIds.Count == 0 ? [] : [documentIds[0]];
    }

    public async Task<List<IDocument>> BuildDocumentsAsync(IRequestInput input, IReadOnlyList<long> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var rows = await LoadDocumentRowsAsync(documentIds, cancellationToken);
        var result = new List<IDocument>(documentIds.Count);

        foreach (var documentId in documentIds)
        {
            if (rows.TryGetValue(documentId, out var row))
            {
                // No set document to normalise against: a document reached through a flat list keeps the
                // attributes it was stored with, which already name its stickerset.
                result.Add(BuildDocument(input, row, null, null, false, false));
            }
        }

        return result;
    }

    public async Task<List<IStickerPack>> BuildPacksForDocumentsAsync(IReadOnlyList<long> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var rows = await LoadDocumentRowsAsync(documentIds, cancellationToken);
        var byEmoticon = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (var documentId in documentIds)
        {
            if (!rows.TryGetValue(documentId, out var row))
            {
                continue;
            }

            var alt = ReadStoredAlt(row);
            if (string.IsNullOrEmpty(alt))
            {
                continue;
            }

            if (!byEmoticon.TryGetValue(alt, out var bucket))
            {
                bucket = [];
                byEmoticon[alt] = bucket;
            }

            bucket.Add(documentId);
        }

        return byEmoticon
            .Select(p => (IStickerPack)new TStickerPack { Emoticon = p.Key, Documents = new TVector<long>(p.Value) })
            .ToList();
    }

    private async Task<InstalledStickerSetDocument?> GetInstalledAsync(long userId, long stickerSetId,
        CancellationToken cancellationToken)
    {
        var overlay = await installedStickerSetStore.GetOverlayAsync(userId, [stickerSetId], cancellationToken);

        return overlay.GetValueOrDefault(stickerSetId);
    }

    private async Task<Dictionary<long, BsonDocument>> LoadDocumentRowsAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var rows = await Documents
            .Find(Builders<BsonDocument>.Filter.In("DocumentId",
                documentIds.Distinct().Select(p => (BsonValue)new BsonInt64(p))))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<long, BsonDocument>(rows.Count);
        foreach (var row in rows)
        {
            result[row.GetInt64("DocumentId")] = row;
        }

        return result;
    }

    private async Task<List<IDocument>> BuildSetDocumentsAsync(IRequestInput input, BsonDocument stickerSetDocument,
        IReadOnlyList<long> documentIds, string? diceEmoticon, CancellationToken cancellationToken)
    {
        var rows = await LoadDocumentRowsAsync(documentIds, cancellationToken);

        return BuildDocuments(input, stickerSetDocument, documentIds, rows, diceEmoticon);
    }

    private List<IDocument> BuildDocuments(IRequestInput input, BsonDocument? stickerSetDocument,
        IReadOnlyList<long> documentIds, Dictionary<long, BsonDocument> rows, string? diceEmoticon)
    {
        var isEmojiSet = stickerSetDocument?.GetBool("Emojis") ?? false;
        var textColor = stickerSetDocument?.GetBool("TextColor") ?? false;
        var isMaskSet = stickerSetDocument?.GetBool("Masks") ?? false;
        var altByDocumentId = stickerSetDocument == null
            ? []
            : StickerSetPackReader.BuildAltByDocumentId(stickerSetDocument, diceEmoticon);

        var setId = stickerSetDocument?.GetInt64("StickerSetId") ?? 0;
        var setAccessHash = setId == 0
            ? 0
            : accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, setId,
                AccessHashType.StickerSet);

        var result = new List<IDocument>(documentIds.Count);
        foreach (var documentId in documentIds)
        {
            if (!rows.TryGetValue(documentId, out var row))
            {
                // A set that references a document nobody stored: dropping it is the only safe option,
                // because a document the client cannot download shows as a permanently blank tile.
                continue;
            }

            result.Add(BuildDocument(input, row,
                stickerSetDocument == null ? null : new TInputStickerSetID { Id = setId, AccessHash = setAccessHash },
                altByDocumentId.GetValueOrDefault(documentId), isEmojiSet, textColor, isMaskSet));
        }

        return result;
    }

    private IDocument BuildDocument(IRequestInput input, BsonDocument row, TInputStickerSetID? stickerset,
        string? altFromPack, bool isEmojiSet, bool textColor, bool isMaskSet = false)
    {
        var documentId = row.GetInt64("DocumentId");

        return new TDocument
        {
            Id = documentId,
            // Per-session, like every other media reference on this server; see AccessHashHelper2.
            AccessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId, documentId,
                AccessHashType.Document),
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = row.GetInt32("Date"),
            MimeType = row.GetString("MimeType", "application/octet-stream"),
            Size = row.GetInt64("Size"),
            // dc_id = 0 points the client at a datacenter that does not exist and makes it spam
            // help.getConfig instead of downloading.
            DcId = row.GetInt32("DcId") > 0 ? row.GetInt32("DcId") : MyTelegramConsts.MediaDcId,
            Thumbs = StickerSetPackReader.ReadThumbs(row),
            VideoThumbs = StickerSetPackReader.ReadVideoThumbs(row),
            Attributes = BuildAttributes(row, stickerset, altFromPack, isEmojiSet, textColor, isMaskSet)
        };
    }

    /// <summary>
    /// The stored attributes, with the sticker classification reconciled against the set the document is
    /// being returned as part of.
    ///
    /// <para>Both halves matter. A custom-emoji set whose documents carry only
    /// <c>documentAttributeSticker</c> renders as plain stickers, and a sticker set whose documents carry
    /// <c>documentAttributeCustomEmoji</c> is rejected outright. And the <c>stickerset</c> field has to
    /// name <i>this</i> set with the caller's own access hash, or long-pressing a sticker cannot open the
    /// pack it came from.</para>
    /// </summary>
    private static TVector<IDocumentAttribute> BuildAttributes(BsonDocument row, TInputStickerSetID? stickerset,
        string? altFromPack, bool isEmojiSet, bool textColor, bool isMaskSet)
    {
        var stored = ReadStoredAttributes(row);

        if (stickerset == null)
        {
            return stored.Count > 0 ? new TVector<IDocumentAttribute>(stored) : [];
        }

        var attributes = stored
            .Where(p => isEmojiSet ? p is not TDocumentAttributeSticker : p is not TDocumentAttributeCustomEmoji)
            .ToList();

        var hasPrimary = isEmojiSet
            ? attributes.Any(p => p is TDocumentAttributeCustomEmoji)
            : attributes.Any(p => p is TDocumentAttributeSticker);

        if (!hasPrimary)
        {
            attributes.Insert(0, isEmojiSet
                ? new TDocumentAttributeCustomEmoji
                {
                    Alt = altFromPack ?? string.Empty,
                    Stickerset = stickerset,
                    Free = true,
                    TextColor = textColor
                }
                : new TDocumentAttributeSticker
                {
                    Alt = altFromPack ?? string.Empty,
                    Stickerset = stickerset,
                    Mask = isMaskSet
                });

            return new TVector<IDocumentAttribute>(attributes);
        }

        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                case TDocumentAttributeCustomEmoji customEmoji:
                    customEmoji.Alt = PreferStoredAlt(customEmoji.Alt, altFromPack);
                    customEmoji.Stickerset = stickerset;
                    customEmoji.TextColor = textColor;
                    break;
                case TDocumentAttributeSticker sticker:
                    sticker.Alt = PreferStoredAlt(sticker.Alt, altFromPack);
                    sticker.Stickerset = stickerset;
                    // mask_coords stays as stored: it is per-sticker, not per-set.
                    sticker.Mask = isMaskSet;
                    break;
            }
        }

        return new TVector<IDocumentAttribute>(attributes);
    }

    /// <summary>
    /// The alt recorded on the document wins over the one derived from the pack it belongs to.
    ///
    /// <para>Telegram's own <c>stickerPack.emoticon</c> carries no U+FE0F variation selector while the
    /// documents' <c>alt</c> does, for 207 of the seeded documents. Deriving alt from the pack therefore
    /// silently stripped it — harmless on Android, which strips FE0F on both sides of the comparison in
    /// <c>MediaDataController.getEmojiAnimatedSticker</c>, but tdlib-based clients (iOS, Desktop, tdweb)
    /// compare the raw string and then fail to find the emoji they asked for.</para>
    /// </summary>
    private static string PreferStoredAlt(string? storedAlt, string? altFromPack)
    {
        return string.IsNullOrEmpty(storedAlt) ? altFromPack ?? string.Empty : storedAlt;
    }

    private static List<IDocumentAttribute> ReadStoredAttributes(BsonDocument row)
    {
        if (!row.TryGetValue("Attributes2", out var value) || value.IsBsonNull)
        {
            return [];
        }

        try
        {
            return [..BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(value.ToJson())];
        }
        catch
        {
            // A row written by an older shape of the serializer. Falling back to the set-derived
            // attributes is better than failing the whole request over one document.
            return [];
        }
    }

    /// <summary>The emoji a document carries, for grouping flat lists into packs.</summary>
    private static string? ReadStoredAlt(BsonDocument row)
    {
        foreach (var attribute in ReadStoredAttributes(row))
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
}
