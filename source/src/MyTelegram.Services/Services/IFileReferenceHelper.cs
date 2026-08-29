namespace MyTelegram.Services.Services;

/// <summary>
/// How a <c>file_reference</c> presented by a client compares to what this server would mint now.
/// </summary>
public enum FileReferenceState
{
    /// <summary>Signature matches and the reference is still inside its lifetime.</summary>
    Valid = 0,

    /// <summary>No bytes at all. Clients normalise a missing reference to an empty one.</summary>
    Empty = 1,

    /// <summary>Wrong length, or the signature does not match this <c>(type, id)</c> pair.</summary>
    Invalid = 2,

    /// <summary>Signature matches but the reference is older than the configured lifetime.</summary>
    Expired = 3
}

/// <summary>
/// What the server does with a reference it considers bad.
/// </summary>
public enum FileReferenceMode
{
    /// <summary>No checking at all. The state the server was in before file references were real.</summary>
    Off = 0,

    /// <summary>
    /// Check and log, but let the request through. Mandatory for the first deployment: every emit path
    /// that still hands out a stale or empty reference shows up here as a log line instead of as media
    /// that no client can load.
    /// </summary>
    LogOnly = 1,

    /// <summary>Answer <c>FILE_REFERENCE_*</c> as the official server does.</summary>
    Enforce = 2
}

/// <summary>
/// Mints and validates the <c>file_reference</c> carried by <c>document</c> and <c>photo</c> objects.
/// See https://corefork.telegram.org/api/file-references
/// </summary>
public interface IFileReferenceHelper
{
    /// <summary>The reference this server currently serves for the given media.</summary>
    byte[] Create(AccessHashType type, long id);

    FileReferenceState Validate(ReadOnlySpan<byte> reference, AccessHashType type, long id);

    /// <summary>
    /// Refuses a reference this server did not issue, or issued too long ago, with the error the
    /// official server answers.
    /// </summary>
    /// <param name="index">
    /// Position of the offending media inside a <c>multi_media</c> / <c>extended_media</c> vector, which
    /// turns the error into the <c>FILE_REFERENCE_%d_EXPIRED</c> form clients parse an index out of.
    /// </param>
    /// <param name="isCover">
    /// Set when the reference belongs to <c>inputMediaDocument.video_cover</c> rather than to the document
    /// itself, so the client repairs the cover instead of the video
    /// (tdlib <c>FileReferenceManager::FileReferenceErrorSource::is_cover_</c>, Android
    /// <c>FileRefController.isFileRefErrorCover</c>).
    /// </param>
    void Check(ReadOnlySpan<byte> reference, AccessHashType type, long id, int? index = null,
        bool isCover = false);

    /// <summary>The configured mode, so a caller can skip work entirely while checking is off.</summary>
    FileReferenceMode Mode { get; }
}
