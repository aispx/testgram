namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// A stored <c>secureValue</c>. Everything in here is end-to-end encrypted by the client; the server
/// keeps it verbatim and never holds a key.
/// See https://corefork.telegram.org/passport/encryption
/// </summary>
public class PassportValueDocument
{
    /// <summary>"{UserId}:{Type}" — one value per type per user, saveSecureValue overwrites.</summary>
    public string Id { get; set; } = null!;

    public long UserId { get; set; }

    /// <summary>The <c>SecureValueType</c> constructor id. Stored as long: BSON has no unsigned int.</summary>
    public long Type { get; set; }

    /// <summary><c>secureData.data</c> — encrypted, padded JSON.</summary>
    public byte[]? Data { get; set; }

    /// <summary><c>secureData.data_hash</c>.</summary>
    public byte[]? DataHash { get; set; }

    /// <summary><c>secureData.secret</c> — the encrypted data secret.</summary>
    public byte[]? DataSecret { get; set; }

    public long? FrontSideFileId { get; set; }

    public long? ReverseSideFileId { get; set; }

    public long? SelfieFileId { get; set; }

    public List<long> FileIds { get; set; } = [];

    public List<long> TranslationFileIds { get; set; } = [];

    /// <summary><c>securePlainPhone.phone</c>, already verified through account.verifyPhone.</summary>
    public string? PlainPhone { get; set; }

    /// <summary><c>securePlainEmail.email</c>, already verified through account.verifyEmail.</summary>
    public string? PlainEmail { get; set; }

    /// <summary>
    /// <c>secureValue.hash</c>. Clients and bots use this value verbatim — it is what
    /// <c>account.acceptAuthorization.value_hashes</c> and <c>secureValueError.hash</c> refer to.
    /// </summary>
    public byte[] Hash { get; set; } = [];

    public int Date { get; set; }

    public IEnumerable<long> AllFileIds()
    {
        if (FrontSideFileId.HasValue) yield return FrontSideFileId.Value;
        if (ReverseSideFileId.HasValue) yield return ReverseSideFileId.Value;
        if (SelfieFileId.HasValue) yield return SelfieFileId.Value;
        foreach (var id in FileIds) yield return id;
        foreach (var id in TranslationFileIds) yield return id;
    }
}

public interface IPassportValueStore
{
    /// <summary>
    /// Stores an <c>inputSecureValue</c>, replacing any previous value of the same type. Files carried
    /// as <c>inputSecureFileUploaded</c> are moved into the passport file store; files carried as
    /// <c>inputSecureFile</c> must already belong to the user (the caller is responsible for the
    /// access-hash check).
    /// </summary>
    Task<PassportValueDocument> SaveAsync(long userId, IInputSecureValue value);

    Task<List<PassportValueDocument>> GetAllAsync(long userId);

    /// <summary>Values of the listed constructor ids, in the order the ids were given.</summary>
    Task<List<PassportValueDocument>> GetAsync(long userId, IReadOnlyCollection<uint> types);

    /// <summary>Deletes the listed types and every file they referenced.</summary>
    Task DeleteAsync(long userId, IReadOnlyCollection<uint> types);

    /// <summary>Deletes every passport value of a user, files included.</summary>
    Task DeleteAllAsync(long userId);

    Task<bool> HasAnyAsync(long userId);

    /// <summary>
    /// Builds the TL <c>secureValue</c> constructors, resolving all referenced files in one query.
    /// The returned <c>secureFile.access_hash</c> values are rewritten per session on the way out by
    /// <c>QueuedObjectMessageSender</c>.
    /// </summary>
    Task<TVector<ISecureValue>> ToSecureValuesAsync(long userId, IReadOnlyList<PassportValueDocument> documents);
}
