namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Enable or disable <a href="https://corefork.telegram.org/api/folders#folder-tags">folder tags »</a>.
/// Possible errors
/// Code Type Description
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleDialogFilterTags"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The value is per user and is read back through <c>messages.dialogFilters.tags_enabled</c>;
/// Android mirrors it into <c>MessagesController.folderTags</c> on every <c>getDialogFilters</c>, so a
/// toggle that is not stored turns itself back on for every session. The live service answers
/// <c>tags_enabled = false</c> for an account without the subscription (measured), which is why the
/// gate is here and not only in the UI.</para>
/// </remarks>
internal sealed class ToggleDialogFilterTagsHandler(ICommandBus commandBus, IUserAppService userAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleDialogFilterTags, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestToggleDialogFilterTags obj)
    {
        var user = await userAppService.GetAsync(input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (user!.Premium != true)
        {
            RpcErrors.RpcErrors403.PremiumAccountRequired.ThrowRpcError();
        }

        var command = new ToggleDialogFilterTagsCommand(
            DialogFilterSettingsId.Create(input.UserId),
            input.ToRequestInfo(),
            input.UserId,
            obj.Enabled);

        await commandBus.PublishAsync(command, CancellationToken.None);

        return null!;
    }
}
