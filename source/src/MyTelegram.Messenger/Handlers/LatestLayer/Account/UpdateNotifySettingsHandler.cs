using MyTelegram.Converters;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Edits notification settings from a given user/group, from all users/all groups.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SETTINGS_INVALID Invalid settings were provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateNotifySettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>This used to pass <c>string.Empty</c> where the sound belongs, so every notification sound a user
/// chose was discarded on the way in — which left the whole
/// <a href="https://corefork.telegram.org/api/ringtones">saved notification sounds</a> surface decorative.
/// It also handled only <c>inputNotifyPeer</c> and threw for the category forms, and the categories are
/// where a sound is normally picked: Android's "Notifications and Sounds" screen sets the tone for all
/// private chats, all groups or all channels at once.</para>
///
/// <para><c>inputPeerNotifySettings</c> carries a single <c>sound</c> while <c>peerNotifySettings</c> reports
/// one per platform, because the server is what splits them: "populating the <c>ios_sound</c>,
/// <c>android_sound</c> or <c>other_sound</c> fields according to the platform where the sound should be
/// played". The platform comes from the session (<c>DeviceType</c>); a session whose platform is unknown
/// writes all three, because a <c>notificationSoundRingtone</c> id means the same thing everywhere and
/// dropping the user's choice is worse than storing it too widely.</para>
/// </remarks>
internal sealed class UpdateNotifySettingsHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    ILogger<UpdateNotifySettingsHandler> logger)
    : RpcResultObjectHandler<RequestUpdateNotifySettings, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateNotifySettings obj)
    {
        var userId = input.UserId;
        var (peerType, peerId) = ResolveTarget(input, obj.Peer);

        var sound = NotificationSoundConverter.ToValue(obj.Settings.Sound);
        var (iosSound, androidSound, otherSound) =
            NotificationSoundConverter.SplitByPlatform(sound, input.DeviceType);

        var storiesSound = NotificationSoundConverter.ToValue(obj.Settings.StoriesSound);
        var (storiesIos, storiesAndroid, storiesOther) =
            NotificationSoundConverter.SplitByPlatform(storiesSound, input.DeviceType);

        if (sound?.Kind == NotificationSoundKind.Ringtone)
        {
            // A ringtone id that is not in the user's saved list is stored as it is rather than refused:
            // Android sends the sound in the same call as mute_until, so answering SETTINGS_INVALID for a
            // list that went stale on another device would also lose the mute. tdlib does not validate it
            // either, and a client whose id no longer resolves falls back to its own default sound.
            logger.LogInformation(
                "User {UserId} set the notification sound of {PeerType} {PeerId} to ringtone {RingtoneId}",
                userId, peerType, peerId, sound.RingtoneId);
        }

        var aggregateId = PeerNotifySettingsId.Create(userId, peerType, peerId);
        var command = new UpdatePeerNotifySettingsCommand(aggregateId, input.ToRequestInfo(), userId, peerType,
            peerId, obj.Settings.ShowPreviews, obj.Settings.Silent, obj.Settings.MuteUntil, string.Empty,
            iosSound, androidSound, otherSound, storiesIos, storiesAndroid, storiesOther);

        await commandBus.PublishAsync(command);

        // Answered by OtherDomainEventHandler once the event has been applied.
        return null!;
    }

    /// <summary>
    /// Which settings row the request addresses. The three category forms are stored under their peer type
    /// with no peer id, which is exactly how <c>account.getNotifySettings</c> reads them back.
    /// </summary>
    private (PeerType PeerType, long PeerId) ResolveTarget(IRequestInput input, IInputNotifyPeer notifyPeer)
    {
        switch (notifyPeer)
        {
            case TInputNotifyPeer inputNotifyPeer:
                // The same resolution the rest of the server uses, including its self normalisation
                // (IInputPeer.ToPeer turns any peer whose id is your own into PeerType.Self, which is also
                // what the Saved Messages dialog id is built from). account.getNotifySettings has to resolve
                // the peer the same way or the settings written here can never be read back.
                var peer = peerHelper.GetPeer(inputNotifyPeer.Peer, input.UserId);

                return (peer.PeerType, peer.PeerId);
            case TInputNotifyUsers:
                return (PeerType.User, 0);
            case TInputNotifyChats:
                return (PeerType.Chat, 0);
            case TInputNotifyBroadcasts:
                return (PeerType.Channel, 0);
            default:
                // inputNotifyForumTopic needs a settings row per topic, and the aggregate id
                // (PeerNotifySettings_{userId}_{peerType}_{peerId}) has nowhere to put the topic id. Storing
                // it as the channel's settings would mute the whole channel instead of one topic, so it is
                // refused with the error this method documents. The previous behaviour was to throw
                // NotImplementedException, which left the request unanswered and the client waiting.
                RpcErrors.RpcErrors400.SettingsInvalid.ThrowRpcError();

                return default;
        }
    }
}
