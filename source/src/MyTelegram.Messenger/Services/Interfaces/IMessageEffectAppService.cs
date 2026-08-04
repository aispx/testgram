namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// One entry of the message effect catalog, as stored in the <c>effects</c> collection.
/// See https://corefork.telegram.org/api/effects
/// </summary>
public sealed record MessageEffect(
    long EffectId,
    string Emoticon,
    bool PremiumRequired,
    int Order,
    MessageEffectDocument? StaticIcon,
    MessageEffectDocument EffectSticker,
    MessageEffectDocument? EffectAnimation);

/// <summary>
/// A document referenced by a <see cref="MessageEffect"/>. Stored denormalized inside the effect
/// record so serving the catalog is a single query, exactly like the reactions catalog.
/// </summary>
public sealed record MessageEffectDocument(
    long DocumentId,
    byte[] FileReference,
    int Date,
    string MimeType,
    long Size,
    int DcId,
    TVector<IPhotoSize> Thumbs);

public interface IMessageEffectAppService
{
    /// <summary>
    /// The whole catalog, ordered as it should be displayed in the effect picker.
    /// Cached in memory: the catalog is static content that only changes when it is re-seeded.
    /// </summary>
    Task<IReadOnlyList<MessageEffect>> GetAllAsync();

    /// <summary>
    /// Looks up a single effect by its id, or <c>null</c> when the id is not part of the catalog.
    /// </summary>
    Task<MessageEffect?> GetAsync(long effectId);

    /// <summary>
    /// Validates an effect id supplied by a client on a send/forward request and returns the
    /// effect id that should actually be stored on the message.
    /// <para>
    /// Effects only exist in 1-on-1 chats, so for any other peer type the effect is dropped
    /// instead of raising an error — that is what the official server does, and raising here would
    /// break clients that keep an effect selected while switching chats.
    /// </para>
    /// </summary>
    /// <exception cref="Exception">
    /// EFFECT_ID_INVALID when the id is unknown, PREMIUM_ACCOUNT_REQUIRED when the effect is
    /// Premium-only and the sender has no active subscription.
    /// </exception>
    Task<long?> ValidateEffectAsync(long? effectId, long senderUserId, PeerType toPeerType);

    /// <summary>
    /// Catalog hash, per https://corefork.telegram.org/api/offsets#hash-generation.
    /// </summary>
    int GetHash(IReadOnlyList<MessageEffect> effects);
}
