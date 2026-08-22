namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Descriptor of a stored Telegram Passport file, one-to-one with the <c>secureFile</c> constructor.
/// See https://corefork.telegram.org/passport/encryption
/// </summary>
public class PassportFileDocument
{
    /// <summary>Server-assigned file id ("_id").</summary>
    public long Id { get; set; }

    public long AccessHash { get; set; }

    public long Size { get; set; }

    public int DcId { get; set; }

    /// <summary>Uploader user id. Passport files are never shared, so this is also the only reader.</summary>
    public long OwnerUserId { get; set; }

    /// <summary>Client-chosen file id used during upload.saveFilePart.</summary>
    public long SourceFileId { get; set; }

    /// <summary>The client's <c>data_hash</c> of the plaintext, echoed back verbatim.</summary>
    public byte[] FileHash { get; set; } = [];

    /// <summary>The client's <c>encrypted_data_secret</c>, echoed back verbatim.</summary>
    public byte[] Secret { get; set; } = [];

    public int Parts { get; set; }

    public int Date { get; set; }
}

/// <summary>One part of a stored passport file; see <see cref="PassportFileDocument"/>.</summary>
public class PassportFilePartDocument
{
    /// <summary>"{FileId}_{PartIndex}"</summary>
    public string Id { get; set; } = null!;

    public long FileId { get; set; }

    public int PartIndex { get; set; }

    /// <summary>Byte offset of this part within the assembled blob.</summary>
    public long Offset { get; set; }

    public byte[] Bytes { get; set; } = [];
}

/// <summary>
/// Local store for Telegram Passport files. The bodies are AES-encrypted by the client with a key the
/// server never sees, so this is a blind relay: the only integrity check performed is the declared
/// MD5 of the encrypted blob. Modelled on <see cref="SecretChat.IEncryptedFileStore"/> — parts are
/// snapshotted out of the mutable <c>file_parts</c> staging collection at store time, because a client
/// reusing its file id would otherwise rewrite the bytes of an already-saved document.
/// </summary>
public interface IPassportFileStore
{
    /// <summary>
    /// Verifies uploaded parts and creates the passport-file record.
    /// Throws FILE_EMTPY (no parts), FILE_PARTS_INVALID (count mismatch / gap / over the size cap),
    /// MD5_CHECKSUM_INVALID.
    /// </summary>
    Task<PassportFileDocument> StoreUploadedAsync(long userId,
        long clientFileId,
        int declaredParts,
        string? md5Checksum,
        byte[] fileHash,
        byte[] secret);

    /// <summary>Looks up a file the user already uploaded, for reuse through <c>inputSecureFile</c>.</summary>
    Task<PassportFileDocument?> GetAsync(long fileId, long ownerUserId);

    /// <summary>Batch form of <see cref="GetAsync"/>, keyed by file id.</summary>
    Task<Dictionary<long, PassportFileDocument>> GetManyAsync(IReadOnlyCollection<long> fileIds, long ownerUserId);

    /// <summary>
    /// Reads only the requested window, touching just the parts that overlap it. Returns null when the
    /// file does not exist.
    /// </summary>
    /// <remarks>
    /// Deliberately not scoped to the owner: a bot the user submitted the form to downloads the very
    /// same file, and the capability that authorises the read is the session-derived access hash the
    /// caller must present (as for secret-chat files). The caller checks it.
    /// </remarks>
    Task<(PassportFileDocument Document, byte[] Bytes)?> LoadRangeAsync(long fileId, long offset, int limit);

    /// <summary>Deletes the listed files and their parts. Ids not owned by the user are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<long> fileIds, long ownerUserId);

    /// <summary>Deletes every passport file of a user (password removal, account deletion).</summary>
    Task DeleteAllAsync(long ownerUserId);
}
