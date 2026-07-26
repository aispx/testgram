namespace MyTelegram.Messenger.Services.SecretChat;

public class EncryptedFileDocument
{
    /// <summary>Server-assigned file id ("_id").</summary>
    public long Id { get; set; }

    public long AccessHash { get; set; }

    public long Size { get; set; }

    public int DcId { get; set; }

    public int KeyFingerprint { get; set; }

    /// <summary>Uploader user id — file_parts rows are keyed by the uploader.</summary>
    public long OwnerUserId { get; set; }

    /// <summary>Client-chosen file id used during upload.saveFilePart.</summary>
    public long SourceFileId { get; set; }

    public int Parts { get; set; }

    public int Date { get; set; }
}

/// <summary>
/// Local store for encrypted files (secret chats). The opaque blob stays in the
/// <c>file_parts</c> collection; this store only records the descriptor and reassembles
/// the blob on download. Bytes are never inspected (blind relay).
/// </summary>
public interface IEncryptedFileStore
{
    /// <summary>
    /// Verifies uploaded parts and creates the encrypted-file record.
    /// Throws FILE_EMTPY (no parts), FILE_PARTS_INVALID (count mismatch), MD5_CHECKSUM_INVALID.
    /// </summary>
    Task<EncryptedFileDescriptor> StoreUploadedAsync(long userId,
        long clientFileId,
        int declaredParts,
        int keyFingerprint,
        string? md5Checksum);

    Task<EncryptedFileDescriptor?> ResolveAsync(long fileId, long accessHash);

    /// <summary>Whole-blob read. Prefer <see cref="LoadRangeAsync"/> on the download path.</summary>
    Task<(EncryptedFileDocument Document, byte[] Blob)?> LoadForDownloadAsync(long fileId, long accessHash);

    /// <summary>
    /// Reads only the requested window, touching just the parts that overlap it, so a chunked
    /// download of a large file stays linear instead of re-materialising the blob per request.
    /// Returns null when the file does not exist or the access hash does not match.
    /// </summary>
    Task<(EncryptedFileDocument Document, byte[] Bytes)?> LoadRangeAsync(long fileId,
        long accessHash,
        long offset,
        int limit);
}
