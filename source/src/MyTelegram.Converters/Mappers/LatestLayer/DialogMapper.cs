namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class DialogMapper
    : IObjectMapper<IDialogReadModel, TDialog>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;
    

    public TDialog Map(IDialogReadModel source)
    {
        return Map(source, new TDialog());
    }

    public TDialog Map(
        IDialogReadModel source,
        TDialog destination
    )
    {
        destination.Pts = source.Pts;
        destination.TopMessage = source.TopMessage;
        destination.Pinned = source.Pinned;
        destination.UnreadCount = source.UnreadCount;
        //destination.UnreadMark = source.u;
        destination.ReadInboxMaxId = source.ReadInboxMaxId;
        destination.ReadOutboxMaxId = source.ReadOutboxMaxId;
        destination.Peer = new Peer(source.ToPeerType, source.ToPeerId).ToPeer();
        //if (source.Draft?.Message?.Length > 0)
        //{
        //    destination.Draft = new TDraftMessage
        //    {
        //        Date = source.Draft.Date,
        //        Message = source.Draft.Message,
        //        NoWebpage = source.Draft.NoWebpage,
        //        ReplyTo = new TInputReplyToMessage
        //        {
        //            ReplyToMsgId = source.Draft.ReplyToMsgId ?? 0
        //        },
        //        Entities = source.Draft.Entities.ToTObject<TVector<IMessageEntity>>(),
        //        InvertMedia = source.Draft.InvertMedia,
        //        Effect = source.Draft.Effect
        //    };
        //}

        //destination.NotifySettings = new TPeerNotifySettings
        //{
        //    ShowPreviews = true,
        //    Silent = false,
        //    //Sound = "default",
        //    MuteUntil = 0
        //};
        if (source.NotifySettings != null)
        {
            destination.NotifySettings = new TPeerNotifySettings
            {
                // These used to be a hardcoded notificationSoundDefault, which told every client that the
                // chat plays the default sound no matter what the user had chosen. Each client reads exactly
                // one of the three, so an absent field is what "use your own default" looks like.
                // See https://corefork.telegram.org/api/ringtones#setting-notification-sounds
                AndroidSound = NotificationSoundConverter.ToTl(source.NotifySettings.AndroidSound),
                IosSound = NotificationSoundConverter.ToTl(source.NotifySettings.IosSound),
                OtherSound = NotificationSoundConverter.ToTl(source.NotifySettings.OtherSound),
                StoriesAndroidSound = NotificationSoundConverter.ToTl(source.NotifySettings.StoriesAndroidSound),
                StoriesIosSound = NotificationSoundConverter.ToTl(source.NotifySettings.StoriesIosSound),
                StoriesOtherSound = NotificationSoundConverter.ToTl(source.NotifySettings.StoriesOtherSound),
                MuteUntil = source.NotifySettings.MuteUntil,
                ShowPreviews = source.NotifySettings.ShowPreviews,
                Silent = source.NotifySettings.Silent
            };
        }
        else
        {
            destination.NotifySettings = new TPeerNotifySettings();
        }

        destination.TtlPeriod = source.TtlPeriod;
        destination.UnreadMentionsCount = source.UnreadMentionsCount;
        destination.UnreadReactionsCount = source.UnreadReactionsCount;
        destination.UnreadPollVotesCount = source.UnreadPollVotesCount;
        destination.FolderId = source.FolderId;
        destination.ViewForumAsMessages = source.ViewForumAsMessages;

        return destination;
    }
}