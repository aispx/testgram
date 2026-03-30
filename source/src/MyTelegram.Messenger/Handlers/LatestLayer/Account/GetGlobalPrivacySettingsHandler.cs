namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get global privacy settings
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getGlobalPrivacySettings"/> </c></para>
/// </summary>
internal sealed class GetGlobalPrivacySettingsHandler(IPrivacyAppService privacyAppService, IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetGlobalPrivacySettings, MyTelegram.Schema.IGlobalPrivacySettings>
{
    protected override async Task<MyTelegram.Schema.IGlobalPrivacySettings> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetGlobalPrivacySettings obj)
    {
        var gps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(input.UserId));
        if (gps == null)
            return new TGlobalPrivacySettings();

        TDisallowedGiftsSettings? disallowed = null;
        if (gps.DisallowUnlimitedStargifts || gps.DisallowLimitedStargifts || gps.DisallowUniqueStargifts || gps.DisallowPremiumGifts)
        {
            disallowed = new TDisallowedGiftsSettings
            {
                DisallowUnlimitedStargifts = gps.DisallowUnlimitedStargifts,
                DisallowLimitedStargifts = gps.DisallowLimitedStargifts,
                DisallowUniqueStargifts = gps.DisallowUniqueStargifts,
                DisallowPremiumGifts = gps.DisallowPremiumGifts,
            };
        }

        return new TGlobalPrivacySettings
        {
            ArchiveAndMuteNewNoncontactPeers = gps.ArchiveAndMuteNewNoncontactPeers,
            HideReadMarks = gps.HideReadMarks,
            KeepArchivedFolders = gps.KeepArchivedFolders,
            KeepArchivedUnmuted = gps.KeepArchivedUnmuted,
            NewNoncontactPeersRequirePremium = gps.NewNoncontactPeersRequirePremium,
            DisallowedGifts = disallowed,
        };
    }
}
