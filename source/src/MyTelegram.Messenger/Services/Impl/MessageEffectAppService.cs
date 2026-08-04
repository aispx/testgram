using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Serves the message effect catalog from the <c>effects</c> MongoDB collection, seeded by
/// <c>scripts/seed_effects.py</c>. See https://corefork.telegram.org/api/effects
/// <para>
/// The catalog is static content shared by every user, so it is loaded once and cached; only a
/// re-seed changes it, and <see cref="CacheDuration"/> bounds how long a stale copy can be served.
/// </para>
/// </summary>
public class MessageEffectAppService(IMongoDatabase database, IUserAppService userAppService)
    : IMessageEffectAppService, ISingletonDependency
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private IReadOnlyList<MessageEffect>? _cache;
    private Dictionary<long, MessageEffect>? _cacheById;
    private DateTime _cacheExpiresAt = DateTime.MinValue;

    private IMongoCollection<BsonDocument> Effects => database.GetCollection<BsonDocument>("effects");

    public async Task<IReadOnlyList<MessageEffect>> GetAllAsync()
    {
        var cache = _cache;
        if (cache != null && DateTime.UtcNow < _cacheExpiresAt)
        {
            return cache;
        }

        await _loadLock.WaitAsync();
        try
        {
            // Another caller may have refreshed the cache while we waited for the lock.
            if (_cache != null && DateTime.UtcNow < _cacheExpiresAt)
            {
                return _cache;
            }

            var docs = await Effects
                .Find(Builders<BsonDocument>.Filter.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("Order"))
                .ToListAsync();

            var effects = new List<MessageEffect>(docs.Count);
            foreach (var doc in docs)
            {
                var effect = TryReadEffect(doc);
                if (effect != null)
                {
                    effects.Add(effect);
                }
            }

            _cache = effects;
            _cacheById = effects.ToDictionary(p => p.EffectId);
            _cacheExpiresAt = DateTime.UtcNow.Add(CacheDuration);

            return effects;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<MessageEffect?> GetAsync(long effectId)
    {
        await GetAllAsync();

        return _cacheById != null && _cacheById.TryGetValue(effectId, out var effect)
            ? effect
            : null;
    }

    public async Task<long?> ValidateEffectAsync(long? effectId, long senderUserId, PeerType toPeerType)
    {
        if (effectId is null or 0)
        {
            return null;
        }

        // Effects are only rendered in 1-on-1 chats, so silently drop them elsewhere.
        if (toPeerType != PeerType.User)
        {
            return null;
        }

        var effect = await GetAsync(effectId.Value);
        if (effect == null)
        {
            RpcErrors.RpcErrors400.EffectIdInvalid.ThrowRpcError();
        }

        if (effect!.PremiumRequired)
        {
            await userAppService.CheckAccountPremiumStatusAsync(senderUserId);
        }

        return effect.EffectId;
    }

    public int GetHash(IReadOnlyList<MessageEffect> effects)
    {
        return TelegramHashHelper.GetInt32Hash(effects.Select(p => p.EffectId));
    }

    /// <summary>
    /// Reads one effect record. A record without a usable <c>effect_sticker</c> cannot be rendered
    /// by any client, so it is skipped rather than served as a half-broken entry.
    /// </summary>
    private static MessageEffect? TryReadEffect(BsonDocument doc)
    {
        if (!doc.TryGetValue("EffectId", out var effectIdValue) ||
            !doc.TryGetValue("Emoticon", out var emoticonValue) ||
            emoticonValue.BsonType != BsonType.String)
        {
            return null;
        }

        var effectSticker = TryReadDocument(doc, "EffectSticker");
        if (effectSticker == null)
        {
            return null;
        }

        return new MessageEffect(
            GetInt64(effectIdValue),
            emoticonValue.AsString,
            doc.GetValue("PremiumRequired", false).ToBoolean(),
            GetInt32(doc.GetValue("Order", 0)),
            TryReadDocument(doc, "StaticIcon"),
            effectSticker,
            TryReadDocument(doc, "EffectAnimation"));
    }

    private static MessageEffectDocument? TryReadDocument(BsonDocument parent, string field)
    {
        if (!parent.TryGetValue(field, out var value) || value.BsonType != BsonType.Document)
        {
            return null;
        }

        var doc = value.AsBsonDocument;
        if (!doc.TryGetValue("Id", out var idValue))
        {
            return null;
        }

        return new MessageEffectDocument(
            GetInt64(idValue),
            GetByteArray(doc.GetValue("FileReference", BsonNull.Value)),
            GetInt32(doc.GetValue("Date", 0)),
            doc.GetValue("MimeType", "application/x-tgsticker").AsString,
            GetInt64(doc.GetValue("Size", 0)),
            GetInt32(doc.GetValue("DcId", 2)),
            ReadThumbs(doc));
    }

    private static TVector<IPhotoSize> ReadThumbs(BsonDocument document)
    {
        var result = new TVector<IPhotoSize>();
        if (!document.TryGetValue("Thumbs", out var thumbsValue) || !thumbsValue.IsBsonArray)
        {
            return result;
        }

        foreach (var value in thumbsValue.AsBsonArray.Where(p => p.IsBsonDocument))
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
                        Size = GetInt32(thumb["Size"])
                    });
                    break;
                case nameof(TPhotoCachedSize):
                    result.Add(new TPhotoCachedSize
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Bytes = GetByteArray(thumb["Bytes"])
                    });
                    break;
                case nameof(TPhotoSizeProgressive):
                    result.Add(new TPhotoSizeProgressive
                    {
                        Type = thumbType,
                        W = GetInt32(thumb["W"]),
                        H = GetInt32(thumb["H"]),
                        Sizes = new TVector<int>(thumb["Sizes"].AsBsonArray.Select(GetInt32))
                    });
                    break;
                case nameof(TPhotoStrippedSize):
                    result.Add(new TPhotoStrippedSize { Type = thumbType, Bytes = GetByteArray(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoPathSize):
                    result.Add(new TPhotoPathSize { Type = thumbType, Bytes = GetByteArray(thumb["Bytes"]) });
                    break;
                case nameof(TPhotoSizeEmpty):
                    result.Add(new TPhotoSizeEmpty { Type = thumbType });
                    break;
            }
        }

        return result;
    }

    private static byte[] GetByteArray(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Binary => value.AsBsonBinaryData.Bytes,
            BsonType.Array => value.AsBsonArray.Select(p => (byte)p.ToInt32()).ToArray(),
            _ => []
        };
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => value.ToInt64()
        };
    }

    private static int GetInt32(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => unchecked((int)value.AsInt64),
            BsonType.Double => (int)value.AsDouble,
            _ => value.ToInt32()
        };
    }
}
