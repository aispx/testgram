namespace MyTelegram.Messenger.Services.SecretChat;

/// <summary>
/// Secret-chat update fan-out. All updates are wrapped in updateShort.
/// Auth key ids are PERMANENT auth key ids (the session server matches
/// onlySendToThisAuthKeyId/excludeAuthKeyId against device.PermAuthKeyId).
/// </summary>
public interface ISecretChatUpdateDispatcher
{
    /// <summary>All devices of the user (used pre-establishment and for discard fan-out).</summary>
    Task PushToAllDevicesAsync(long userId, IUpdate update, long? excludeAuthKeyId = null, PushData? pushData = null);

    /// <summary>Exactly one bound device. qts is set only for updateNewEncryptedMessage.</summary>
    Task PushToDeviceAsync(long userId,
        long permAuthKeyId,
        IUpdate update,
        int? qts = null,
        PushData? pushData = null);
}
