namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class UserMapper
    : IObjectMapper<IUserReadModel, TUser>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;
    

    public TUser Map(IUserReadModel source)
    {
        return Map(source, new TUser());
    }

    public TUser Map(
        IUserReadModel source,
        TUser destination
    )
    {
        destination.Id = source.UserId;
        destination.Photo = new TUserProfilePhotoEmpty();
        destination.AccessHash = source.AccessHash;
        destination.Bot = source.Bot;
        destination.BotInfoVersion = source.BotInfoVersion;
        destination.Username = source.UserName;
        destination.Phone = source.PhoneNumber;
        destination.FirstName = source.FirstName;
        destination.LastName = source.LastName;
        destination.Fake = source.Fake;
        destination.Scam = source.Scam;
        destination.Verified = source.Verified;
        destination.Support = source.Support;
        destination.Premium = source.Premium;

        destination.Color = source.Color.ToPeerColor();
        destination.ProfileColor = source.ProfileColor.ToPeerColor();
        destination.ContactRequirePremium = source.GlobalPrivacySettings?.NewNoncontactPeersRequirePremium ?? false;
        if (source.GlobalPrivacySettings?.NoncontactPeersPaidStars > 0)
            destination.SendPaidMessagesStars = source.GlobalPrivacySettings.NoncontactPeersPaidStars;
        destination.BotHasMainApp = source.BotHasMainApp;
        destination.BotActiveUsers = source.BotActiveUsers;

        // Read BotBusiness from MongoDB directly since it's not in IUserReadModel
        // This is a temporary solution until BotBusiness is added to the read model
        if (source.Bot)
        {
            // BotBusiness flag will be set by UserConverterService
            destination.BotBusiness = false; // Default value, will be overridden if needed
        }

        return destination;
    }
}