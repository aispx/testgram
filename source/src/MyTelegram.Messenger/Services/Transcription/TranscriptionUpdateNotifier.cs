namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>
/// Pushes <c>updateTranscribedAudio</c> once recognition finishes.
///
/// <para><b>The requesting session is not excluded</b>, which is the opposite of every other notifier
/// here (<c>SavedRingtoneUpdateNotifier</c> and friends drop the caller because it already holds the RPC
/// result). Here the caller holds a <i>pending</i> result and the update is the only thing that
/// completes it: tdlib parks the promise in <c>speech_recognition_queries_</c> and fails it after 60
/// seconds if no update arrives (<c>AUDIO_TRANSCRIPTION_TIMEOUT</c>), and Android's
/// <c>TranscribeButton.finishTranscription</c> is what stops the spinner.</para>
///
/// <para><b>The <c>transcription_id</c> is the only thing that matches an update to a request.</b> tdlib
/// looks the id up in <c>pending_audio_transcriptions_</c> and returns silently when it is unknown —
/// "flags_, peer_ and msg_id_ must not be used" says so in its own source — so the id here has to be the
/// one the RPC handed out, and it must never be 0. The peer and message id are still filled in, because
/// Android writes the text into <c>MessagesStorage</c> keyed by them, which is how a <i>second</i> device
/// picks the transcription up.</para>
///
/// <para>The update carries no <c>pts</c>, and tdlib handles it outside the pts machinery
/// (<c>UpdatesManager::on_update</c> answers the promise immediately), so no sequence number is
/// consumed.</para>
/// See https://corefork.telegram.org/constructor/updateTranscribedAudio
/// </summary>
public interface ITranscriptionUpdateNotifier
{
    Task NotifyAsync(TranscriptionDocument document, string text, bool pending);
}

/// <inheritdoc />
public class TranscriptionUpdateNotifier(IObjectMessageSender objectMessageSender, IPeerHelper peerHelper)
    : ITranscriptionUpdateNotifier, ITransientDependency
{
    public Task NotifyAsync(TranscriptionDocument document, string text, bool pending)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateTranscribedAudio
            {
                Pending = pending,
                Peer = peerHelper.ToPeer(document.PeerType, document.PeerId),
                MsgId = document.MsgId,
                TranscriptionId = document.TranscriptionId,
                Text = text
            }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        return objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, document.RequestedByUserId), updates);
    }
}
