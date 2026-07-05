namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Helper for building TUser objects for Star Ref affiliate bots
/// </summary>
internal static class StarRefBotUserHelper
{
    public static IUser BuildBotUser(IUserConverterService userConverterService, IRequestInput input, IUserReadModel user)
    {
        var converted = userConverterService.ToUser(input, user, layer: input.Layer);
        if (converted is TUser tUser)
        {
            tUser.Bot = true;
        }

        return converted;
    }
}
