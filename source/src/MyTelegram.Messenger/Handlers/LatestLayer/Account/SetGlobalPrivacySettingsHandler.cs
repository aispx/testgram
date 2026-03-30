namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Set global privacy settings
/// <para><c>See <a href="https://corefork.telegram.org/method/account.setGlobalPrivacySettings"/> </c></para>
/// </summary>
internal sealed class SetGlobalPrivacySettingsHandler(ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSetGlobalPrivacySettings, MyTelegram.Schema.IGlobalPrivacySettings>
{
    protected override async Task<IGlobalPrivacySettings> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSetGlobalPrivacySettings obj)
    {
        var d = obj.Settings.DisallowedGifts as TDisallowedGiftsSettings;
        var command = new UpdateGlobalPrivacySettingsCommand(UserId.Create(input.UserId), input.ToRequestInfo(), new GlobalPrivacySettings(
            obj.Settings.ArchiveAndMuteNewNoncontactPeers,
            obj.Settings.KeepArchivedUnmuted,
            obj.Settings.KeepArchivedFolders,
            obj.Settings.HideReadMarks,
            obj.Settings.NewNoncontactPeersRequirePremium,
            obj.Settings.NoncontactPeersPaidStars,
            d?.DisallowUnlimitedStargifts ?? false,
            d?.DisallowLimitedStargifts ?? false,
            d?.DisallowUniqueStargifts ?? false,
            d?.DisallowPremiumGifts ?? false));
        await commandBus.PublishAsync(command);
        return new TGlobalPrivacySettings
        {
            ArchiveAndMuteNewNoncontactPeers = obj.Settings.ArchiveAndMuteNewNoncontactPeers,
            HideReadMarks = obj.Settings.HideReadMarks,
            KeepArchivedFolders = obj.Settings.KeepArchivedFolders,
            KeepArchivedUnmuted = obj.Settings.KeepArchivedUnmuted,
            NewNoncontactPeersRequirePremium = obj.Settings.NewNoncontactPeersRequirePremium,
            NoncontactPeersPaidStars = obj.Settings.NoncontactPeersPaidStars,
            DisallowedGifts = obj.Settings.DisallowedGifts,
        };
    }
}
