namespace MyTelegram.Messenger.Services.Privacy;

/// <summary>
/// Shared masking applied when a viewer is not allowed to see a privacy-protected field.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
/// <remarks>
/// Kept in one place because the same masking has to happen on every path that exposes the
/// field — <c>users.getUsers</c>, <c>contacts.getStatuses</c>, updates — and previously each
/// path either reimplemented it or forgot it entirely.
/// </remarks>
public static class PrivacyMaskingHelper
{
    /// <summary>
    /// Coarsens an exact last seen timestamp to <c>userStatusRecently</c>, which is what the
    /// official server reports to viewers disallowed by <c>privacyKeyStatusTimestamp</c>.
    /// </summary>
    public static IUserStatus MaskStatusTimestamp(IUserStatus? status)
    {
        return status switch
        {
            // Already coarse enough to leak nothing precise.
            TUserStatusRecently recently => recently,
            TUserStatusLastWeek lastWeek => lastWeek,
            TUserStatusLastMonth lastMonth => lastMonth,
            TUserStatusEmpty empty => empty,
            _ => new TUserStatusRecently()
        };
    }

    /// <inheritdoc cref="MaskStatusTimestamp"/>
    public static void HideStatusTimestamp(ILayeredUser user)
    {
        user.Status = MaskStatusTimestamp(user.Status);
    }
}
