using Microsoft.Extensions.Logging;

namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class ChannelMapper
    : IObjectMapper<IChannelReadModel, TChannel>,
        ILayeredMapper,
        ITransientDependency
{
    private readonly ILogger<ChannelMapper> _logger;

    public ChannelMapper(ILogger<ChannelMapper> logger)
    {
        _logger = logger;
    }

    public int Layer => Layers.LayerLatest;
    

    public TChannel Map(IChannelReadModel source)
    {
        return Map(source, new TChannel());
    }

    public TChannel Map(
        IChannelReadModel source,
        TChannel destination
    )
    {
        destination.Id = source.ChannelId;
        destination.Title = source.Title;
        destination.ParticipantsCount = source.ParticipantsCount;
        destination.Broadcast = source.Broadcast;
        // Fix for old channels without Megagroup flag: if not broadcast, it's a megagroup
        destination.Megagroup = source.MegaGroup || (!source.Broadcast && !source.MegaGroup);

        _logger.LogInformation("ChannelMapper: ChannelId={ChannelId}, Title={Title}, source.MegaGroup={SourceMegaGroup}, source.Broadcast={SourceBroadcast}, destination.Megagroup={DestMegagroup}",
            source.ChannelId, source.Title, source.MegaGroup, source.Broadcast, destination.Megagroup);
        destination.Verified = source.Verified;
        destination.Fake = source.Fake;
        destination.Scam = source.Scam;
        destination.Signatures = source.Signatures;
        destination.AccessHash = source.AccessHash;
        destination.Username = source.UserName;
        destination.Date = source.Date;
        destination.SlowmodeEnabled = source.SlowModeEnabled;

        destination.DefaultBannedRights = (source.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights())
            .ToChatBannedRights();

        destination.HasLink = source.HasLink;
        destination.Noforwards = source.NoForwards;
        destination.Color = source.Color.ToPeerColor();
        destination.ProfileColor = source.ProfileColor.ToPeerColor();
        destination.Forum = source.Forum;

        // NOTE: source.BackgroundEmojiId is the peer color's pattern emoji
        // (https://core.telegram.org/api/colors) and is already exposed to clients
        // inside destination.Color / destination.ProfileColor. It must NOT be mapped
        // onto destination.EmojiStatus, which is the channel's status emoji badge.

        destination.Level = 10;

        if (source.EmojiStatus != null)
        {
            destination.EmojiStatus = new TEmojiStatus
            {
                DocumentId = source.EmojiStatus.DocumentId,
                Until = source.EmojiStatus.Until
            };
        }

        destination.SignatureProfiles = source.SignatureProfiles;
        destination.SubscriptionUntilDate = source.SubscriptionUntilDate;

        // Use Usernames if available (with Editable/Active fields)
        if (source.Usernames != null && source.Usernames.Count > 0)
        {
            destination.Usernames = new TVector<IUsername>();
            foreach (var usernameInfo in source.Usernames)
            {
                destination.Usernames.Add(new TUsername
                {
                    Username = usernameInfo.Username,
                    Editable = usernameInfo.Editable,
                    Active = usernameInfo.Active
                });
            }

            // Set primary username (first active editable, or first active)
            var primary = source.Usernames.FirstOrDefault(u => u.Active && u.Editable)
                          ?? source.Usernames.FirstOrDefault(u => u.Active);
            if (primary != null)
            {
                destination.Username = primary.Username;
            }
        }
        else
        {
            // Fallback to legacy UserName field
            destination.Username = source.UserName;
        }

        destination.JoinRequest = source.JoinRequest;
        destination.JoinToSend = source.JoinToSend;
        destination.Monoforum = source.IsMonoforum;
        destination.BroadcastMessagesAllowed = source.BroadcastMessagesAllowed;
        if (source.LinkedMonoforumId.HasValue) destination.LinkedMonoforumId = source.LinkedMonoforumId;
        if (source.SendPaidMessagesStars.HasValue) destination.SendPaidMessagesStars = source.SendPaidMessagesStars.Value;

        return destination;
    }
}
