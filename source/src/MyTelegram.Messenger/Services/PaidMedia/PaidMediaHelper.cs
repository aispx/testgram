using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MyTelegram.Messenger.Services.PaidMedia;

public sealed class PaidMediaDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public long OwnerPeerId { get; set; }
    public int MsgId { get; set; }
    public long ChannelId { get; set; }
    public long SellerUserId { get; set; }
    public long SenderUserId { get; set; }
    public long StarsAmount { get; set; }
    public string? Payload { get; set; }
    public List<byte[]> ExtendedMedia { get; set; } = [];
    public List<long> BuyerUserIds { get; set; } = [];
    public int Date { get; set; }
}

public sealed record PaidMediaInvoiceContext(PaidMediaDocument Document, Peer DisplayPeer, int DisplayMsgId);

public static class PaidMediaHelper
{
    public const long MaxStarsAmount = 2500;
    public const string CollectionName = "paid-media";
    private static readonly byte[] PaidMediaFallbackPathThumb =
    [
        // SvgHelper.decompress() on Android prepends "M" and appends "z".
        // These bytes produce: M0,0H512V512H0z.  The paid-media client accepts
        // photoPathSize/stripped thumbs for hidden previews, but not regular
        // photoSize objects that point to downloadable files.
        0, 128, 0, 199, 5, 1, 2, 213, 5, 1, 2, 199, 0
    ];

    public static string MakeId(long ownerPeerId, int msgId) => $"{ownerPeerId}-{msgId}";

    public static TVector<IMessageExtendedMedia> ToRevealedExtendedMedia(IEnumerable<byte[]> mediaBytes)
    {
        return new TVector<IMessageExtendedMedia>(mediaBytes.Select(bytes =>
        {
            var memory = new ReadOnlyMemory<byte>(bytes);
            return (IMessageExtendedMedia)new TMessageExtendedMedia
            {
                Media = PreparePurchasedMedia(memory.Read<IMessageMedia>())
            };
        }).ToList());
    }

    public static TVector<IMessageMedia> ToTransactionExtendedMedia(IEnumerable<byte[]> mediaBytes)
    {
        return new TVector<IMessageMedia>(mediaBytes.Select(bytes =>
        {
            var memory = new ReadOnlyMemory<byte>(bytes);
            return PreparePurchasedMedia(memory.Read<IMessageMedia>());
        }).ToList());
    }

    private static IMessageMedia PreparePurchasedMedia(IMessageMedia media)
    {
        // Paid media has its own locked/unlocked presentation.  If the original
        // uploaded photo/video had the regular Telegram spoiler flag, Desktop
        // keeps drawing the already purchased media as a spoiler unless we strip
        // that flag from the revealed payload.  Keep the stored bytes untouched;
        // sanitize only the object returned to clients and transaction history.
        switch (media)
        {
            case TMessageMediaPhoto photo:
                photo.Spoiler = false;
                photo.Flags &= ~(1 << 3);
                break;
            case TMessageMediaDocument document:
                document.Spoiler = false;
                document.Flags &= ~(1 << 4);
                break;
        }

        return media;
    }

    public static bool IsAllowedInputMedia(IInputMedia media)
    {
        return media is TInputMediaPhoto
            or TInputMediaUploadedPhoto
            or TInputMediaPhotoExternal
            or TInputMediaDocument
            or TInputMediaUploadedDocument
            or TInputMediaDocumentExternal;
    }

    public static IMessageExtendedMedia ToPreview(IMessageMedia media, byte[]? strippedThumb = null)
    {
        var preview = new TMessageExtendedMediaPreview();
        IEnumerable<IPhotoSize>? thumbs = null;

        if (media is TMessageMediaPhoto { Photo: TPhoto photo })
        {
            var size = photo.Sizes?.OfType<TPhotoSize>().OrderByDescending(x => x.W * x.H).FirstOrDefault();
            if (size != null)
            {
                preview.W = size.W;
                preview.H = size.H;
            }
            thumbs = photo.Sizes;
        }
        else if (media is TMessageMediaDocument { Document: TDocument doc })
        {
            var video = doc.Attributes?.OfType<TDocumentAttributeVideo>().FirstOrDefault();
            if (video != null)
            {
                preview.W = video.W;
                preview.H = video.H;
                preview.VideoDuration = (int)Math.Ceiling(video.Duration);
            }
            thumbs = doc.Thumbs;
        }

        preview.Thumb = strippedThumb is { Length: > 3 }
            ? new TPhotoStrippedSize { Type = "i", Bytes = strippedThumb }
            : GetPaidMediaPreviewThumb(thumbs);
        return preview;
    }

    private static IPhotoSize GetPaidMediaPreviewThumb(IEnumerable<IPhotoSize>? sizes)
    {
        return sizes?.OfType<TPhotoStrippedSize>().FirstOrDefault()
            ?? (IPhotoSize?)sizes?.OfType<TPhotoPathSize>().FirstOrDefault()
            ?? new TPhotoPathSize
            {
                Type = "j",
                Bytes = PaidMediaFallbackPathThumb
            };
    }


    public static async Task<byte[]?> TryCreatePreviewThumbFromUploadAsync(IMongoDatabase db, long userId, IInputMedia media)
    {
        var file = media switch
        {
            TInputMediaUploadedPhoto uploadedPhoto => uploadedPhoto.File,
            TInputMediaUploadedDocument { Thumb: not null } uploadedDocument => uploadedDocument.Thumb,
            TInputMediaUploadedDocument uploadedDocument => uploadedDocument.File,
            _ => null
        };

        if (file is not TInputFile and not TInputFileBig)
            return null;

        var uploadBytes = await TryReadUploadedFileBytesAsync(db, userId, file);
        return uploadBytes == null ? null : TryCreateStrippedThumb(uploadBytes);
    }

    private static async Task<byte[]?> TryReadUploadedFileBytesAsync(IMongoDatabase db, long userId, IInputFile file)
    {
        var (fileId, expectedParts) = file switch
        {
            TInputFile inputFile => (inputFile.Id, inputFile.Parts),
            TInputFileBig inputFileBig => (inputFileBig.Id, inputFileBig.Parts),
            _ => (0L, 0)
        };

        if (fileId == 0 || expectedParts <= 0)
            return null;

        var parts = await db.GetCollection<BsonDocument>("file_parts")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("FileId", fileId)))
            .Sort(Builders<BsonDocument>.Sort.Ascending("FilePart"))
            .ToListAsync();

        if (parts.Count == 0)
            return null;

        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            if (!part.TryGetValue("Bytes", out var bytesValue) || !bytesValue.IsBsonBinaryData)
                return null;

            var bytes = bytesValue.AsBsonBinaryData.Bytes;
            ms.Write(bytes, 0, bytes.Length);
        }

        return ms.ToArray();
    }


    public static async Task<byte[]?> TryCreatePreviewThumbFromStoredMediaAsync(IMongoDatabase db, IMessageMedia media)
    {
        try
        {
            if (media is TMessageMediaPhoto { Photo: TPhoto photo })
            {
                var size = photo.Sizes?.OfType<TPhotoSize>()
                    .OrderByDescending(x => (long)x.W * x.H)
                    .FirstOrDefault();

                if (size == null)
                    return null;

                var bytes = await TryReadStoredFileBytesAsync(db, photo.Id, size.Type);
                return bytes == null ? null : TryCreateStrippedThumb(bytes);
            }

            if (media is TMessageMediaDocument { Document: TDocument document })
            {
                var thumb = document.Thumbs?.OfType<TPhotoSize>()
                    .OrderByDescending(x => (long)x.W * x.H)
                    .FirstOrDefault();

                if (thumb == null)
                    return null;

                var bytes = await TryReadStoredFileBytesAsync(db, document.Id, thumb.Type);
                return bytes == null ? null : TryCreateStrippedThumb(bytes);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static byte[]? TryCreateStrippedThumb(byte[] fileBytes)
    {
        try
        {
            using var image = Image.Load(fileBytes);
            var size = GetPreviewSize(image.Width, image.Height);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(size.Width, size.Height),
                Mode = ResizeMode.Max
            }));

            using var jpegStream = new MemoryStream();
            image.Save(jpegStream, new JpegEncoder { Quality = 40 });
            var jpegBytes = jpegStream.ToArray();
            var payload = ExtractJpegScanPayload(jpegBytes);
            if (payload == null || payload.Length == 0)
                return null;

            var result = new byte[payload.Length + 3];
            result[0] = 1;
            result[1] = (byte)Math.Clamp(image.Height, 1, 255);
            result[2] = (byte)Math.Clamp(image.Width, 1, 255);
            Buffer.BlockCopy(payload, 0, result, 3, payload.Length);
            return result;
        }
        catch
        {
            return null;
        }
    }


    private static async Task<byte[]?> TryReadStoredFileBytesAsync(IMongoDatabase db, long fileId, string? sizeType)
    {
        if (fileId == 0 || string.IsNullOrWhiteSpace(sizeType))
            return null;

        var fileDoc = await db.GetCollection<BsonDocument>("eventflow-filereadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("FileId", fileId))
            .FirstOrDefaultAsync();

        if (fileDoc == null ||
            !fileDoc.TryGetValue("Key", out var keyValue) || !keyValue.IsBsonBinaryData ||
            !fileDoc.TryGetValue("Iv", out var ivValue) || !ivValue.IsBsonBinaryData)
            return null;

        var encrypted = await TryDownloadStoredObjectAsync(fileId, sizeType);
        if (encrypted == null || encrypted.Length == 0)
            return null;

        var decrypted = new byte[encrypted.Length];
        AesCtrTransform(encrypted, decrypted, keyValue.AsBsonBinaryData.Bytes, ivValue.AsBsonBinaryData.Bytes);
        return decrypted;
    }

    private static async Task<byte[]?> TryDownloadStoredObjectAsync(long fileId, string sizeType)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("App__PaidMediaPreviewMinioEndpoint")
                ?? Environment.GetEnvironmentVariable("Minio__Endpoint")
                ?? "http://minio:9000";
            var bucket = Environment.GetEnvironmentVariable("App__PaidMediaPreviewMinioBucket")
                ?? Environment.GetEnvironmentVariable("Minio__BucketName")
                ?? "tg-files";

            if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = $"http://{endpoint}";
            }

            endpoint = endpoint.TrimEnd('/');
            var objectName = Uri.EscapeDataString($"{fileId}_{sizeType}");
            using var response = await PreviewHttpClient.GetAsync($"{endpoint}/{bucket}/{objectName}");
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
    }

    private static void AesCtrTransform(ReadOnlySpan<byte> input, Span<byte> output, byte[] key, byte[] iv)
    {
        if (key is not { Length: 32 } || iv is not { Length: 16 })
            return;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var encryptor = aes.CreateEncryptor();
        Span<byte> counter = stackalloc byte[16];
        iv.CopyTo(counter);

        var keyStream = new byte[16];
        var counterBlock = new byte[16];
        var offset = 0;
        while (offset < input.Length)
        {
            counter.CopyTo(counterBlock);
            encryptor.TransformBlock(counterBlock, 0, counterBlock.Length, keyStream, 0);
            var count = Math.Min(16, input.Length - offset);
            for (var i = 0; i < count; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keyStream[i]);

            IncrementCounter(counter);
            offset += count;
        }
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    private static readonly HttpClient PreviewHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static (int Width, int Height) GetPreviewSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (1, 1);

        const int maxSide = 40;
        var ratio = Math.Min((double)maxSide / width, (double)maxSide / height);
        if (ratio > 1)
            ratio = 1;

        return (Math.Clamp((int)Math.Round(width * ratio), 1, 255), Math.Clamp((int)Math.Round(height * ratio), 1, 255));
    }

    private static byte[]? ExtractJpegScanPayload(byte[] jpegBytes)
    {
        var scanStart = -1;
        for (var i = 0; i < jpegBytes.Length - 1; i++)
        {
            if (jpegBytes[i] == 0xff && jpegBytes[i + 1] == 0xda)
            {
                if (i + 4 > jpegBytes.Length)
                    return null;

                var segmentLength = (jpegBytes[i + 2] << 8) | jpegBytes[i + 3];
                scanStart = i + 2 + segmentLength;
                break;
            }
        }

        if (scanStart < 0 || scanStart >= jpegBytes.Length)
            return null;

        var end = -1;
        for (var i = jpegBytes.Length - 2; i > scanStart; i--)
        {
            if (jpegBytes[i] == 0xff && jpegBytes[i + 1] == 0xd9)
            {
                end = i;
                break;
            }
        }

        if (end <= scanStart)
            return null;

        return jpegBytes[scanStart..end];
    }

    public static async Task<PaidMediaDocument?> FindAsync(IMongoDatabase db, long ownerPeerId, int msgId)
    {
        return await db.GetCollection<PaidMediaDocument>(CollectionName)
            .Find(x => x.Id == MakeId(ownerPeerId, msgId))
            .FirstOrDefaultAsync();
    }

    public static async Task<PaidMediaInvoiceContext?> ResolveInvoiceAsync(
        IMongoDatabase db,
        IPeerHelper peerHelper,
        IRequestInput input,
        TInputInvoiceMessage invoiceMessage)
    {
        var displayPeer = peerHelper.GetPeer(invoiceMessage.Peer, input.UserId);
        return await ResolveMessageAsync(db, displayPeer, input.UserId, invoiceMessage.MsgId);
    }

    public static async Task<PaidMediaInvoiceContext?> ResolveMessageAsync(
        IMongoDatabase db,
        Peer displayPeer,
        long currentUserId,
        int msgId)
    {
        var ownerPeerId = GetReadModelOwnerPeerId(displayPeer, currentUserId);

        var direct = await FindAsync(db, ownerPeerId, msgId);
        if (direct != null)
            return new PaidMediaInvoiceContext(direct, displayPeer, msgId);

        var messageDoc = await FindMessageDocumentAsync(db, displayPeer, currentUserId, msgId);
        if (messageDoc == null || !IsPaidMediaMessage(messageDoc))
            return null;

        foreach (var candidate in GetOriginalMessageCandidates(messageDoc))
        {
            var doc = await FindAsync(db, candidate.PeerId, candidate.MsgId);
            if (doc != null)
                return new PaidMediaInvoiceContext(doc, displayPeer, msgId);
        }

        return null;
    }

    private static long GetReadModelOwnerPeerId(Peer peer, long currentUserId)
    {
        return peer.PeerType == PeerType.Self ? currentUserId : peer.PeerId;
    }

    private static async Task<BsonDocument?> FindMessageDocumentAsync(IMongoDatabase db, Peer displayPeer, long currentUserId, int msgId)
    {
        var collection = db.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var messageIdFilter = Builders<BsonDocument>.Filter.Eq("MessageId", msgId);
        var ownerPeerId = GetReadModelOwnerPeerId(displayPeer, currentUserId);

        var peerFilter = displayPeer.PeerType switch
        {
            PeerType.User or PeerType.Self => Builders<BsonDocument>.Filter.Eq("OwnerPeerId", ownerPeerId),
            PeerType.Channel or PeerType.Chat => Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", ownerPeerId),
                Builders<BsonDocument>.Filter.Eq("ToPeerId", ownerPeerId)),
            _ => Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", ownerPeerId),
                Builders<BsonDocument>.Filter.Eq("ToPeerId", ownerPeerId))
        };

        return await collection.Find(Builders<BsonDocument>.Filter.And(messageIdFilter, peerFilter)).FirstOrDefaultAsync();
    }

    private static IEnumerable<(long PeerId, int MsgId)> GetOriginalMessageCandidates(BsonDocument messageDoc)
    {
        var savedPeerId = GetInt64(messageDoc, "SavedFromPeerId");
        var savedMsgId = GetInt32(messageDoc, "SavedFromMsgId");
        if (savedPeerId.HasValue && savedMsgId.HasValue)
            yield return (savedPeerId.Value, savedMsgId.Value);

        var postChannelId = GetInt64(messageDoc, "PostChannelId");
        var postMessageId = GetInt32(messageDoc, "PostMessageId");
        if (postChannelId.HasValue && postMessageId.HasValue)
            yield return (postChannelId.Value, postMessageId.Value);

        if (messageDoc.TryGetValue("FwdHeader", out var fwdHeaderValue) && fwdHeaderValue.IsBsonDocument)
        {
            var fwdHeader = fwdHeaderValue.AsBsonDocument;
            var savedFromPeer = GetNestedPeerId(fwdHeader, "SavedFromPeer");
            var savedFromMsgId = GetInt32(fwdHeader, "SavedFromMsgId");
            if (savedFromPeer.HasValue && savedFromMsgId.HasValue)
                yield return (savedFromPeer.Value, savedFromMsgId.Value);

            var fromPeer = GetNestedPeerId(fwdHeader, "FromId");
            var channelPost = GetInt32(fwdHeader, "ChannelPost");
            if (fromPeer.HasValue && channelPost.HasValue)
                yield return (fromPeer.Value, channelPost.Value);
        }
    }

    private static bool IsPaidMediaMessage(BsonDocument messageDoc)
    {
        if (messageDoc.TryGetValue("Media2", out var media2) && media2.IsBsonDocument)
        {
            var mediaDoc = media2.AsBsonDocument;
            return mediaDoc.TryGetValue("_t", out var typeValue) && typeValue.IsString && typeValue.AsString == nameof(TMessageMediaPaidMedia);
        }

        if (!messageDoc.TryGetValue("Media", out var media) || media.IsBsonNull || !media.IsBsonBinaryData)
            return false;

        try
        {
            var mediaBuffer = new ReadOnlyMemory<byte>(media.AsBsonBinaryData.Bytes);
            return mediaBuffer.Read<IMessageMedia>() is TMessageMediaPaidMedia;
        }
        catch
        {
            return false;
        }
    }

    private static long? GetNestedPeerId(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value) || !value.IsBsonDocument)
            return null;

        return GetInt64(value.AsBsonDocument, "PeerId");
    }

    private static long? GetInt64(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value) || value.IsBsonNull)
            return null;

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            _ => null
        };
    }

    private static int? GetInt32(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value) || value.IsBsonNull)
            return null;

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => checked((int)value.AsInt64),
            _ => null
        };
    }

    public static bool IsPurchasedBy(PaidMediaDocument doc, long userId)
    {
        return doc.SenderUserId == userId || doc.BuyerUserIds.Contains(userId);
    }

    public static async Task MarkPurchasedAsync(IMongoDatabase db, PaidMediaDocument doc, long userId)
    {
        await db.GetCollection<PaidMediaDocument>(CollectionName).UpdateOneAsync(
            x => x.Id == doc.Id,
            Builders<PaidMediaDocument>.Update.AddToSet(x => x.BuyerUserIds, userId));
    }

    public static async Task<int> NextQtsAsync(IMongoDatabase db, long userId)
    {
        var counters = db.GetCollection<BsonDocument>("counters");
        var result = await counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"qts_{userId}"),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });
        return result["seq"].AsInt32;
    }
}
