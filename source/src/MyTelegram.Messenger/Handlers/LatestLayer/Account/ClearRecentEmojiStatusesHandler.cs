namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Clears list of recently used <a href="https://corefork.telegram.org/api/emoji-status">emoji statuses</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.clearRecentEmojiStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ClearRecentEmojiStatusesHandler(
    ICommandBus commandBus,
    IUserAppService userAppService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestClearRecentEmojiStatuses, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestClearRecentEmojiStatuses obj)
    {
        await commandBus.PublishAsync(new ClearRecentEmojiStatusesCommand(
            UserId.Create(input.UserId),
            input.ToRequestInfo()));
        userAppService.InvalidateCache(input.UserId);

        // The rpc result and the updateRecentEmojiStatuses push telling the other sessions are
        // emitted by UserDomainEventHandler once UserRecentEmojiStatusesClearedEvent is committed.
        return null!;
    }
}
