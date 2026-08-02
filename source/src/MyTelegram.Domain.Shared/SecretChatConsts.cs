// ReSharper disable once CheckNamespace

namespace MyTelegram;

public static class SecretChatConsts
{
    /// <summary>
    /// Fixed positive initial qts value assigned to every Authorization_Key's
    /// secret-chat temporary update box. Identical for all Authorization_Keys.
    /// See Requirements 12.3, 13.5.
    /// </summary>
    public const int QtsInitialValue = 1;

    /// <summary>
    /// Smallest structurally valid <c>data:bytes</c> for messages.sendEncrypted*.
    /// The outer envelope is key_fingerprint (8) + msg_key (16) + at least one AES block (16),
    /// and those 24 leading bytes are identical for MTProto 1.0 and 2.0 secret chats.
    /// This is the only part of the payload a blind relay can check: key_fingerprint rotates
    /// during PFS re-keying and msg_key needs the shared key, so neither may be validated.
    /// </summary>
    public const int MinEncryptedPayloadLength = 40;

    /// <summary>
    /// How long an allocated-but-uncommitted qts holds the delivered watermark down before the
    /// sequencer assumes its sender died and steps over it.
    /// <para>
    /// The allocation is registered atomically with the <c>$inc</c> and released by
    /// <c>SetQtsAsync</c>. If the sender crashes in between, nothing ever releases it, so the entry
    /// must expire or the device's watermark would be wedged forever. On expiry the value is BURNT —
    /// a permanent hole in the numbering — which is safe because every consumer filters qts as a
    /// range, never for contiguity.
    /// </para>
    /// </summary>
    public static readonly TimeSpan QtsAllocationStaleAfter = TimeSpan.FromSeconds(60);
}
