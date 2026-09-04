using MyTelegram.Messenger.Services.Transcription;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Transcription;

/// <summary>
/// Feature: the queue and the text cache behind
/// <a href="https://corefork.telegram.org/api/transcribe">messages.transcribeAudio</a>.
///
/// <para>
/// Three things are load bearing. A repeat call must answer with the <b>same</b>
/// <c>transcription_id</c> — tdlib matches an <c>updateTranscribedAudio</c> to a request by that id alone
/// ("flags_, peer_ and msg_id_ must not be used") and asserts on a zero one. A <b>failed</b> row must not
/// be handed back as the answer, because tapping the button again is the only retry a user has. And the
/// text is cached per <b>document</b>, not per message, so the same voice note forwarded into ten chats is
/// recognised once.
/// </para>
/// </summary>
public class TranscriptionStoreTests
{
    private const long UserId = 2_010_001;
    private const long DocumentId = 5_204_474_871_112_567_500;

    private static TranscriptionDocument Row(string id, long transcriptionId, bool trialConsumed = true)
    {
        return new TranscriptionDocument
        {
            Id = id,
            OwnerPeerId = UserId,
            MsgId = 32_002,
            PeerId = 2_010_002,
            PeerType = PeerType.User,
            RequestedByUserId = UserId,
            DocumentId = DocumentId,
            TranscriptionId = transcriptionId,
            TrialConsumed = trialConsumed,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    [RequiresMongoDbFact]
    public async Task A_queued_transcription_is_claimed_once()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.EnqueueAsync(Row("message_1", 111));

        var first = await store.ClaimAsync(4, leaseSeconds: 120);
        var second = await store.ClaimAsync(4, leaseSeconds: 120);

        first.ShouldHaveSingleItem().TranscriptionId.ShouldBe(111);
        second.ShouldBeEmpty();
    }

    /// <summary>
    /// Two devices tapping the same message at the same time: the row that won owns the id, and both
    /// clients have to be told that one or one of them waits for an update it will never recognise.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_concurrent_request_gets_the_transcription_id_that_won()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        var first = await store.EnqueueAsync(Row("message_1", 111));
        var second = await store.EnqueueAsync(Row("message_1", 222));

        second.TranscriptionId.ShouldBe(first.TranscriptionId);
    }

    /// <summary>
    /// A failed row is replaced, not returned: returning it would answer with an empty final transcription
    /// that nothing will ever fill in, and the button could never be retried.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_failed_transcription_is_requeued_with_a_new_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.EnqueueAsync(Row("message_1", 111));
        await store.FailAsync("message_1");

        var retried = await store.EnqueueAsync(Row("message_1", 222));

        retried.TranscriptionId.ShouldBe(222);
        retried.Pending.ShouldBeTrue();

        var stored = await store.GetAsync("message_1");
        stored!.Failed.ShouldBeFalse();
        stored.Pending.ShouldBeTrue();

        (await store.ClaimAsync(4, leaseSeconds: 120)).ShouldHaveSingleItem().TranscriptionId.ShouldBe(222);
    }

    [RequiresMongoDbFact]
    public async Task A_failed_transcription_is_not_claimed_again()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.EnqueueAsync(Row("message_1", 111));
        await store.FailAsync("message_1");

        (await store.ClaimAsync(4, leaseSeconds: 120)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_completed_transcription_keeps_its_text_and_is_not_claimed_again()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.EnqueueAsync(Row("message_1", 111));
        await store.ClaimAsync(4, leaseSeconds: 120);
        await store.CompleteAsync("message_1", "привет");

        var stored = await store.GetAsync("message_1");
        stored!.Text.ShouldBe("привет");
        stored.Pending.ShouldBeFalse();

        (await store.ClaimAsync(4, leaseSeconds: 120)).ShouldBeEmpty();
    }

    /// <summary>
    /// A worker that died mid-job must not park the row forever: the lease expires and the next pass picks
    /// it up, with the attempt count carried forward so it cannot be retried without end.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_expired_lease_is_claimed_again_and_counts_the_attempt()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.EnqueueAsync(Row("message_1", 111));

        (await store.ClaimAsync(4, leaseSeconds: 120)).ShouldHaveSingleItem().Attempts.ShouldBe(1);

        await store.ReleaseAsync("message_1", attempts: 1,
            nextAttemptDate: (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);

        (await store.ClaimAsync(4, leaseSeconds: 120)).ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    /// <summary>
    /// Keyed by document, not by message: the same body forwarded on is one recognition, and that is what
    /// makes a repeat call from a cleared cache free.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_text_cache_is_keyed_by_document()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.SaveCachedTextAsync(DocumentId, "привет", "russian");

        (await store.GetCachedTextAsync(DocumentId)).ShouldBe("привет");
        (await store.GetCachedTextAsync(DocumentId + 1)).ShouldBeNull();
    }

    /// <summary>
    /// An empty transcript is cached too. A voice note with no speech in it has been recognised; asking the
    /// provider again would cost the same and answer the same.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_empty_transcript_is_cached_rather_than_treated_as_absent()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = Store(mongo);

        await store.SaveCachedTextAsync(DocumentId, string.Empty, null);

        (await store.GetCachedTextAsync(DocumentId)).ShouldBe(string.Empty);
    }

    private static TranscriptionStore Store(EmbeddedMongoServer mongo)
    {
        return new TranscriptionStore(mongo.Database,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TranscriptionStore>.Instance);
    }
}
