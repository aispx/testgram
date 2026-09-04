using MyTelegram.Messenger.Services.Transcription;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Rate <a href="https://corefork.telegram.org/api/transcribe">transcribed voice message</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.rateTranscribedAudio"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para><b>The method documents no errors, and that is deliberate on the clients' side too.</b> Each of
/// them marks the transcription as rated locally without waiting to see whether the call succeeded
/// (tdesktop's <c>markTranscriptionAsRated</c> runs next to <c>.send()</c>, iOS's <c>withDidRate()</c>
/// inside the same transaction), so refusing a rating whose <c>transcription_id</c> no longer matches
/// would leave the client believing it had been recorded anyway. A mismatch is therefore logged and
/// answered <c>boolTrue</c> — the rating is simply not attributed to a transcription this server has no
/// record of.</para>
/// </remarks>
internal sealed class RateTranscribedAudioHandler(
    IPeerHelper peerHelper,
    ITranscriptionStore store,
    ITranscriptionRatingStore ratingStore,
    ILogger<RateTranscribedAudioHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestRateTranscribedAudio, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestRateTranscribedAudio obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            return new TBoolTrue();
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var transcription = await store.GetAsync(MessageId.Create(ownerPeerId, obj.MsgId).Value);

        if (transcription == null || transcription.TranscriptionId != obj.TranscriptionId)
        {
            logger.LogWarning(
                "User {UserId} rated transcription {TranscriptionId} of message {MsgId}, which this server does not hold",
                input.UserId, obj.TranscriptionId, obj.MsgId);

            return new TBoolTrue();
        }

        await ratingStore.SaveAsync(input.UserId, obj.TranscriptionId, transcription.DocumentId, obj.Good);

        logger.LogInformation("User {UserId} rated transcription {TranscriptionId} as {Rating}",
            input.UserId, obj.TranscriptionId, obj.Good ? "good" : "bad");

        return new TBoolTrue();
    }
}
