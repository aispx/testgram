using MyTelegram.Messenger.Services.Transcription;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// <a href="https://corefork.telegram.org/api/transcribe">Transcribe voice message</a>
/// Possible errors
/// Code Type Description
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 MSG_VOICE_MISSING The specified message is not a voice message.
/// 400 MSG_VOICE_TOO_LONG The specified voice message is too long to be transcribed.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// 400 TRANSCRIPTION_FAILED Audio transcription failed.
/// 420 FLOOD_WAIT_X A wait of X seconds is required (the free trial is used up).
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.transcribeAudio"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para><b>The answer is <c>pending</c>, and that is not a shortcut.</b> tdlib caps this request at
/// eight seconds (<c>TranscribeAudioQuery::send</c> sets <c>total_timeout_limit_ = 8</c>), which no
/// download plus transcode plus recognition round trip fits into. So the work is queued for
/// <see cref="TranscriptionBackgroundService"/> and the text arrives as <c>updateTranscribedAudio</c>,
/// which is exactly the flow the API documents.</para>
///
/// <para><b>A repeat call is free.</b> Clients cache a finished transcription and never re-ask (tdlib's
/// <c>recognize_speech</c> returns early on <c>is_transcribed_</c>, Android checks
/// <c>voiceTranscriptionFinal</c>), so a second call means a cleared cache or another device — and it
/// answers from the stored row with the same <c>transcription_id</c>, without spending a trial try.</para>
///
/// <para><b>The exhausted trial is <c>FLOOD_WAIT_%d</c>.</b> Every client turns that error into the
/// cooldown it displays: Android's <c>TranscribeButton</c> sets the remaining count to 0 and the cooldown
/// to <c>now + X</c>, tdlib does the same through <c>Global::get_retry_after</c>, iOS maps it to
/// <c>limitExceeded</c>. <c>PREMIUM_ACCOUNT_REQUIRED</c> is reserved for a deployment that has switched
/// the trial off entirely, where there is no cooldown to report.</para>
/// </remarks>
internal sealed class TranscribeAudioHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    ITranscriptionStore store,
    ITranscriptionEligibility eligibility,
    ISpeechRecognitionClient speechRecognitionClient,
    ILogger<TranscribeAudioHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestTranscribeAudio,
        MyTelegram.Schema.Messages.ITranscribedAudio>
{
    protected override async Task<MyTelegram.Schema.Messages.ITranscribedAudio> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Messages.RequestTranscribeAudio obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // A channel numbers its messages once; a private chat numbers them per user, so the caller's own
        // box is the only place obj.MsgId means anything. Same resolution as the other peer + msg_id
        // methods (DeleteFactCheckHandler, EditFactCheckHandler).
        var ownerPeerId = peer!.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageId = MessageId.Create(ownerPeerId, obj.MsgId).Value;

        // A channel's messages are addressed by the channel's own id, so without this the method would
        // read out a voice note from any channel whose id and message id were guessed. A private chat needs
        // no such check: the box is the caller's own.
        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync((long?)peer.PeerId);
            if (channel == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNoReadAccessAsync(input, channel!))
            {
                return null!;
            }
        }

        var message = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(messageId));
        if (message == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        var media = TranscribableMediaResolver.Resolve(message!);
        if (media == null)
        {
            RpcErrors.RpcErrors400.MsgVoiceMissing.ThrowRpcError();
        }

        // Before anything is charged: a row that already exists is the answer, whether it is finished or
        // still queued. A failed one is not - tapping the button again is the only retry a user has.
        var existing = await store.GetAsync(messageId);
        if (existing is { Failed: false })
        {
            return Respond(existing.TranscriptionId, existing.Text, existing.Pending, null, null);
        }

        // The same body may already have been recognised in another chat or for another account. Answering
        // from that costs nothing, so it happens before the quota is consulted at all: charging a try for
        // text the server already holds would be indefensible, and so would refusing it as too long.
        var cached = await store.GetCachedTextAsync(media!.DocumentId);
        if (cached != null)
        {
            var completed = await store.SaveCompletedAsync(NewDocument(input, obj, peer, ownerPeerId, messageId,
                media, trialConsumed: false, text: cached));

            return Respond(completed.TranscriptionId, cached, false, null, null);
        }

        if (!speechRecognitionClient.IsEnabled)
        {
            // Honest rather than a permanent spinner: nothing would ever pick the row up.
            logger.LogWarning(
                "Refused to transcribe document {DocumentId}: speech recognition is not configured " +
                "(App__Transcription__ApiKey/Model/BaseUrl)", media.DocumentId);
            RpcErrors.RpcErrors400.TranscriptionFailed.ThrowRpcError();
        }

        var allowance = await eligibility.EvaluateAsync(input.UserId, peer, media.DurationSeconds);

        switch (allowance.Allowance)
        {
            case TranscriptionAllowance.Exhausted:
                RpcErrors.RpcErrors420.FloodWaitX.ThrowRpcError(allowance.RetryAfterSeconds);
                break;
            case TranscriptionAllowance.PremiumRequired:
                RpcErrors.RpcErrors403.PremiumAccountRequired.ThrowRpcError();
                break;
            case TranscriptionAllowance.TooLong:
                // Refused at the number this caller was advertised - transcribe_audio_trial_duration_max
                // for a trial call - and refused before the counter was touched.
                TranscribeExtraRpcErrors.MsgVoiceTooLong.ThrowRpcError();
                break;
        }

        var queued = await store.EnqueueAsync(NewDocument(input, obj, peer, ownerPeerId, messageId, media,
            trialConsumed: allowance.Allowance == TranscriptionAllowance.Trial));

        logger.LogInformation(
            "Queued a transcription of document {DocumentId} for user {UserId} (transcription {TranscriptionId}, {Duration}s, allowance {Allowance})",
            media.DocumentId, input.UserId, queued.TranscriptionId, media.DurationSeconds, allowance.Allowance);

        return Respond(queued.TranscriptionId, queued.Text, true, allowance.Remaining, allowance.ResetDate);
    }

    private static TranscriptionDocument NewDocument(IRequestInput input,
        MyTelegram.Schema.Messages.RequestTranscribeAudio obj, Peer peer, long ownerPeerId, string messageId,
        TranscribableMedia media, bool trialConsumed, string text = "")
    {
        return new TranscriptionDocument
        {
            Id = messageId,
            OwnerPeerId = ownerPeerId,
            MsgId = obj.MsgId,
            PeerId = peer.PeerId,
            PeerType = peer.PeerType,
            RequestedByUserId = input.UserId,
            DocumentId = media.DocumentId,
            MimeType = media.MimeType,
            TranscriptionId = NewTranscriptionId(),
            Text = text,
            TrialConsumed = trialConsumed,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    /// <summary>
    /// Never 0: tdlib rejects a zero id outright (<c>"Receive no transcription identifier"</c>) and
    /// asserts on one arriving in an update. Random rather than derived from the message, so
    /// <c>messages.rateTranscribedAudio</c> cannot be aimed at somebody else's transcription by guessing.
    /// </summary>
    private static long NewTranscriptionId()
    {
        return Random.Shared.NextInt64(1, long.MaxValue);
    }

    /// <summary>
    /// <paramref name="trialRemaining"/> and <paramref name="trialResetDate"/> share flag bit 1 of
    /// <c>messages.transcribedAudio</c>, so they travel together or not at all - one without the other
    /// cannot even be serialized. iOS also reads the date as the cooldown exactly when the count is 0
    /// and <i>clears</i> its stored cooldown when the date is missing.
    /// </summary>
    private static MyTelegram.Schema.Messages.ITranscribedAudio Respond(long transcriptionId, string text,
        bool pending, int? trialRemaining, int? trialResetDate)
    {
        return new TTranscribedAudio
        {
            Pending = pending,
            TranscriptionId = transcriptionId,
            Text = text,
            TrialRemainsNum = trialRemaining.HasValue && trialResetDate.HasValue ? trialRemaining : null,
            TrialRemainsUntilDate = trialRemaining.HasValue && trialResetDate.HasValue ? trialResetDate : null
        };
    }
}
