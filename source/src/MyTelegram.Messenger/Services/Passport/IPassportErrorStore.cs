namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// One <c>SecureValueError</c> a bot reported through <c>users.setSecureValueErrors</c>. The errors are
/// kept per (user, bot) pair and handed back through <c>account.authorizationForm.errors</c> so the user
/// can see what the service rejected. See https://corefork.telegram.org/api/passport
/// </summary>
public class PassportErrorDocument
{
    /// <summary>"{UserId}:{BotId}:{Kind}:{Type}:{HashKey}" — resending the same error overwrites it.</summary>
    public string Id { get; set; } = null!;

    public long UserId { get; set; }

    public long BotId { get; set; }

    /// <summary>The <c>SecureValueError</c> constructor id.</summary>
    public long Kind { get; set; }

    /// <summary>The <c>SecureValueType</c> constructor id the error is about.</summary>
    public long Type { get; set; }

    /// <summary>The single hash carried by every constructor except the two "files" ones.</summary>
    public byte[]? Hash { get; set; }

    /// <summary>The hashes carried by <c>secureValueErrorFiles</c> / <c>...TranslationFiles</c>.</summary>
    public List<byte[]> Hashes { get; set; } = [];

    /// <summary>Only <c>secureValueErrorData</c> carries a field name.</summary>
    public string? Field { get; set; }

    public string Text { get; set; } = string.Empty;

    public int Date { get; set; }
}

public interface IPassportErrorStore
{
    /// <summary>
    /// Replaces the errors the bot previously reported for this user with the given set. An empty set
    /// clears them, which is how a bot retracts an error it no longer considers valid.
    /// </summary>
    Task SetAsync(long userId, long botId, IReadOnlyList<ISecureValueError> errors);

    /// <summary>Errors the bot reported, rebuilt as TL constructors.</summary>
    Task<TVector<ISecureValueError>> GetAsync(long userId, long botId);

    /// <summary>Drops every error of this (user, bot) pair — the form was resubmitted.</summary>
    Task ClearAsync(long userId, long botId);

    /// <summary>Drops every error of a user (password removal, account deletion).</summary>
    Task ClearAllAsync(long userId);
}
