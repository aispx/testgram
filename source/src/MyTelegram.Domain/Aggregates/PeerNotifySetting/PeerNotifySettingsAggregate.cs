namespace MyTelegram.Domain.Aggregates.PeerNotifySetting;

[EnableAutoGeneration]
public class PeerNotifySettingsAggregate : SnapshotAggregateRoot<PeerNotifySettingsAggregate, PeerNotifySettingsId,
    PeerNotifySettingsSnapshot>
{
    private readonly PeerNotifySettingsState _state = new();

    public PeerNotifySettingsAggregate(PeerNotifySettingsId id) : base(id, SnapshotEveryFewVersionsStrategy.Default)
    {
        Register(_state);
    }

    /// <summary>
    /// Applies the settings a client sent. Every parameter is optional on the wire, and an absent one means
    /// "leave this as it is" — <c>inputPeerNotifySettings</c> has a flag per field and clients send only what
    /// they are changing. Replacing the whole object instead, which this used to do, means muting a chat
    /// clears its notification sound and choosing a sound unmutes the chat.
    /// See https://corefork.telegram.org/api/ringtones#setting-notification-sounds
    /// </summary>
    public void UpdatePeerNotifySettings(RequestInfo requestInfo,
        long ownerPeerId,
        PeerType peerType,
        long peerId,
        bool? showPreviews,
        bool? silent,
        int? muteUntil,
        string? sound,
        NotificationSoundValue? iosSound = null,
        NotificationSoundValue? androidSound = null,
        NotificationSoundValue? otherSound = null,
        NotificationSoundValue? storiesIosSound = null,
        NotificationSoundValue? storiesAndroidSound = null,
        NotificationSoundValue? storiesOtherSound = null)
    {
        var current = _state.PeerNotifySettings ?? PeerNotifySettings.DefaultSettings;

        var peerNotifySettings = new PeerNotifySettings(
            showPreviews ?? current.ShowPreviews,
            silent ?? current.Silent,
            muteUntil ?? current.MuteUntil,
            string.IsNullOrEmpty(sound) ? current.Sound : sound,
            iosSound ?? current.IosSound,
            androidSound ?? current.AndroidSound,
            otherSound ?? current.OtherSound,
            storiesIosSound ?? current.StoriesIosSound,
            storiesAndroidSound ?? current.StoriesAndroidSound,
            storiesOtherSound ?? current.StoriesOtherSound);

        Emit(new PeerNotifySettingsUpdatedEvent(requestInfo,
            ownerPeerId,
            peerType,
            peerId,
            peerNotifySettings));
    }

    protected override Task<PeerNotifySettingsSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new PeerNotifySettingsSnapshot(_state.PeerNotifySettings));
    }

    protected override Task LoadSnapshotAsync(PeerNotifySettingsSnapshot snapshot,
        ISnapshotMetadata metadata,
        CancellationToken cancellationToken)
    {
        _state.LoadSnapshot(snapshot);
        return Task.CompletedTask;
    }
}