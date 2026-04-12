namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Helper for building TUser objects for Star Ref affiliate bots
/// </summary>
internal static class StarRefBotUserHelper
{
    public static IUser BuildBotUser(IUserReadModel user)
    {
        return new TUser
        {
            Id = user.UserId,
            AccessHash = user.AccessHash,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Username = user.UserName,
            Phone = user.PhoneNumber,
            Photo = new TUserProfilePhotoEmpty(),
            Status = new TUserStatusEmpty(),
            Bot = true,
            RestrictionReason = new TVector<IRestrictionReason>()
        };
    }
}
