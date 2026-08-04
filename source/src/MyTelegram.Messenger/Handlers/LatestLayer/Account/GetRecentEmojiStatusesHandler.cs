namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get recently used <a href="https://corefork.telegram.org/api/emoji-status">emoji statuses</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getRecentEmojiStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRecentEmojiStatusesHandler(IUserAppService userAppService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetRecentEmojiStatuses, MyTelegram.Schema.Account.IEmojiStatuses>
{
    protected override async Task<MyTelegram.Schema.Account.IEmojiStatuses> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetRecentEmojiStatuses obj)
    {
        var user = await userAppService.GetAsync(input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Projected from UserEmojiStatusUpdatedEvent, already most recent first and deduplicated.
        var documentIds = user!.RecentEmojiStatuses?.Where(p => p != 0).ToList() ?? [];

        return EmojiStatusesHelper.ToEmojiStatuses(documentIds, obj.Hash);
    }
}
