// ReSharper disable once CheckNamespace

namespace MyTelegram;

/// <summary>
/// Server-side descriptor of a stored encrypted file (secret chats).
/// The file content itself is an opaque blob assembled from uploaded file parts.
/// </summary>
public sealed record EncryptedFileDescriptor(long Id,
    long AccessHash,
    long Size,
    int DcId,
    int KeyFingerprint);
