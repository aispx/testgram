namespace MyTelegram.Messenger.Services.SecretChat;

/// <summary>
/// Resolved secret chat plus the caller's role in it.
/// </summary>
public sealed record SecretChatAccess(IEncryptedChatReadModel Chat, bool CallerIsAdmin)
{
    public long OtherUserId => CallerIsAdmin ? Chat.ParticipantId : Chat.AdminId;

    public long OtherPermAuthKeyId => CallerIsAdmin ? Chat.ParticipantPermAuthKeyId : Chat.AdminPermAuthKeyId;

    public long CallerUserId => CallerIsAdmin ? Chat.AdminId : Chat.ParticipantId;
}

/// <summary>
/// Common access control for all secret-chat methods. The check order is fixed and
/// short-circuits at the first failure: caller type, chat resolution, access_hash, membership.
/// </summary>
public interface ISecretChatAccessResolver
{
    /// <summary>Step 1 only: rejects anonymous callers and bots.</summary>
    Task EnsureUserCallerAsync(IRequestInput input);

    /// <summary>Steps 1-4 for methods taking an InputEncryptedChat (verifies access_hash).</summary>
    Task<SecretChatAccess> ResolveAsync(IRequestInput input, IInputEncryptedChat peer);

    /// <summary>Steps 1, 2, 4 for discardEncryption, whose TL carries no access_hash.</summary>
    Task<SecretChatAccess> ResolveByChatIdAsync(IRequestInput input, long chatId);

    /// <summary>
    /// Requires the chat to be established. Discarded yields ENCRYPTION_DECLINED for send
    /// operations and ENCRYPTION_ID_INVALID otherwise; Waiting always yields ENCRYPTION_ID_INVALID.
    /// </summary>
    void RequireActive(SecretChatAccess access, bool forSend);
}
