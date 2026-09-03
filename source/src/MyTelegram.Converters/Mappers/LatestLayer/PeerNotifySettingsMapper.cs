namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class PeerNotifySettingsMapper
    : IObjectMapper<PeerNotifySettings, TPeerNotifySettings>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;
    

    public TPeerNotifySettings Map(PeerNotifySettings source)
    {
        return Map(source, new TPeerNotifySettings());
    }

    public TPeerNotifySettings Map(
        PeerNotifySettings source,
        TPeerNotifySettings destination
    )
    {
        destination.ShowPreviews = source.ShowPreviews;
        destination.Silent = source.Silent;
        destination.MuteUntil = source.MuteUntil;

        // Every client reads exactly one of these three and falls back to its own default when the field is
        // absent, so leaving them unset (as this did) makes a chosen notification sound invisible even after
        // it was stored. See https://corefork.telegram.org/api/ringtones#setting-notification-sounds
        destination.IosSound = NotificationSoundConverter.ToTl(source.IosSound);
        destination.AndroidSound = NotificationSoundConverter.ToTl(source.AndroidSound);
        destination.OtherSound = NotificationSoundConverter.ToTl(source.OtherSound);
        destination.StoriesIosSound = NotificationSoundConverter.ToTl(source.StoriesIosSound);
        destination.StoriesAndroidSound = NotificationSoundConverter.ToTl(source.StoriesAndroidSound);
        destination.StoriesOtherSound = NotificationSoundConverter.ToTl(source.StoriesOtherSound);
        //destination.StoriesMuted = source.StoriesMuted;
        //destination.StoriesHideSender = source.StoriesHideSender;

        return destination;
    }
}