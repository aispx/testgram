using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>One queued or finished transcription, as it is stored.</summary>
public sealed class TranscriptionDocument
{
    /// <summary>
    /// <c>MessageId.Create(ownerPeerId, msgId).Value</c> — the caller-relative identity of the message.
    /// Private chats number messages per user, so a key built from the dialog peer and the client's own
    /// <c>msg_id</c> would collide between the two sides of a conversation.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public long OwnerPeerId { get; set; }

    public int MsgId { get; set; }

    /// <summary>The peer as the client named it, echoed back in <c>updateTranscribedAudio.peer</c>.</summary>
    public long PeerId { get; set; }

    public PeerType PeerType { get; set; }

    /// <summary>Whose sessions the update is pushed to.</summary>
    public long RequestedByUserId { get; set; }

    public long DocumentId { get; set; }

    /// <summary>
    /// The document's declared mime type. The worker needs it to decide whether the recognition provider
    /// takes the body as it is (Deepgram does, for OGG and MP4) or whether ffmpeg has to convert it first.
    /// </summary>
    public string? MimeType { get; set; }

    /// <summary>Never 0 — tdlib rejects a zero id and asserts on one in an update.</summary>
    public long TranscriptionId { get; set; }

    public string Text { get; set; } = string.Empty;

    public bool Pending { get; set; }

    public bool Failed { get; set; }

    public int Attempts { get; set; }

    /// <summary>Unix seconds until which a worker holds this row.</summary>
    public int ClaimedUntil { get; set; }

    /// <summary>
    /// Whether a free-trial try was spent on this row, so it can be handed back if recognition fails.
    /// </summary>
    public bool TrialConsumed { get; set; }

    public int Date { get; set; }
}

/// <summary>
/// The two collections behind <a href="https://corefork.telegram.org/api/transcribe">transcription</a>:
/// <c>transcriptions</c>, one row per message box, and <c>transcription_texts</c>, the recognised text
/// keyed by document id.
///
/// <para>The split is what makes a repeat call free. Clients cache a finished transcription and never
/// re-ask (tdlib's <c>recognize_speech</c> returns early on <c>is_transcribed_</c>, Android checks
/// <c>voiceTranscriptionFinal</c>), so a second <c>messages.transcribeAudio</c> comes from a cleared
/// cache or another device — and must answer with the stored text and the same
/// <c>transcription_id</c> without spending a try. The text cache is keyed by document because the
/// bytes are what was recognised: the same voice note forwarded on is one recognition.</para>
/// </summary>
public interface ITranscriptionStore
{
    Task<TranscriptionDocument?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the row for a transcription that has to be recognised. A live row for the same message wins
    /// (two devices tapping at once must be told the same <c>transcription_id</c>); a <i>failed</i> one is
    /// replaced, because tapping the button again is how a user retries.
    /// </summary>
    Task<TranscriptionDocument> EnqueueAsync(TranscriptionDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Stores an already known text, so nothing is queued.</summary>
    Task<TranscriptionDocument> SaveCompletedAsync(TranscriptionDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Takes up to <paramref name="max"/> pending rows whose lease has expired.</summary>
    Task<List<TranscriptionDocument>> ClaimAsync(int max, int leaseSeconds,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(string id, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the row failed and final. The transcription stays in the collection rather than being
    /// deleted: Android has already written an empty final transcription into its own storage, so a
    /// retry from that client will never come, and the row is the record of what happened.
    /// </summary>
    Task FailAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Puts the row back for another attempt at <paramref name="nextAttemptDate"/>.</summary>
    Task ReleaseAsync(string id, int attempts, int nextAttemptDate, CancellationToken cancellationToken = default);

    Task<string?> GetCachedTextAsync(long documentId, CancellationToken cancellationToken = default);

    Task SaveCachedTextAsync(long documentId, string text, string? language,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the index the claim loop scans. Called once by the worker at startup.</summary>
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);
}
