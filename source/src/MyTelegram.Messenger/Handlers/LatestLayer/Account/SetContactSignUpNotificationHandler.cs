namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Toggle contact sign up notifications
/// <para><c>See <a href="https://corefork.telegram.org/method/account.setContactSignUpNotification"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetContactSignUpNotificationHandler(ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSetContactSignUpNotification, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSetContactSignUpNotification obj)
    {
        // The TL flag is inverted with respect to what we store and what
        // account.getContactSignUpNotification returns: silent = do not notify.
        await commandBus.PublishAsync(new UpdateContactSignUpNotificationCommand(
            UserId.Create(input.UserId),
            input.ToRequestInfo(),
            !obj.Silent));

        return new TBoolTrue();
    }
}