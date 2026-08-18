namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Resolves the banned rights that actually apply to a channel member right now.
/// A restriction carries an <c>until_date</c>: once that moment has passed the user regains the
/// rights without any further admin action, so every read of the stored flags has to be filtered
/// through here instead of trusting <c>BannedRights != 0</c>.
/// See https://corefork.telegram.org/api/rights
/// </summary>
public static class BannedRightsHelper
{
    /// <summary>
    /// The banned rights currently in force for the member, or <c>null</c> when the member was never
    /// restricted or the restriction has already lapsed.
    /// </summary>
    public static ChatBannedRights? GetEffectiveBannedRights(IChannelMemberReadModel? channelMemberReadModel,
        int now)
    {
        if (channelMemberReadModel == null || channelMemberReadModel.BannedRights == 0)
        {
            return null;
        }

        var bannedRights = ChatBannedRights.FromValue(channelMemberReadModel.BannedRights,
            channelMemberReadModel.UntilDate);

        return bannedRights.IsExpired(now) ? null : bannedRights;
    }

    /// <summary>
    /// True when the member is still kicked/banned from the channel: <c>view_messages</c> is set and
    /// the restriction has not expired yet. An expired ban must not block re-joining.
    /// </summary>
    public static bool IsCurrentlyKicked(IChannelMemberReadModel? channelMemberReadModel, int now)
    {
        return GetEffectiveBannedRights(channelMemberReadModel, now)?.ViewMessages == true;
    }
}
