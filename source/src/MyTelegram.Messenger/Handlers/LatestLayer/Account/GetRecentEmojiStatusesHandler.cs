using MyTelegram.Schema.Account;

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
        if (input.UserId == 0)
        {
            return new TEmojiStatuses
            {
                Hash = 0,
                Statuses = new TVector<IEmojiStatus>()
            };
        }

        var user = await userAppService.GetAsync(input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        if (user!.RecentEmojiStatuses?.Count > 0)
        {
            var hash = ComputeHash(user.RecentEmojiStatuses);
            if (obj.Hash != 0 && obj.Hash == hash)
            {
                return new TEmojiStatusesNotModified();
            }

            return new TEmojiStatuses
            {
                Hash = hash,
                Statuses = new TVector<IEmojiStatus>(user.RecentEmojiStatuses.Select(p => new TEmojiStatus { DocumentId = p }).ToList())
            };
        }

        return new TEmojiStatuses
        {
            Hash = 0,
            Statuses = new TVector<IEmojiStatus>()
        };
    }

    private static long ComputeHash(IEnumerable<long> statuses)
    {
        unchecked
        {
            var hash = 0L;
            foreach (var status in statuses)
            {
                hash = (hash * 20261) + status;
            }

            return hash;
        }
    }
}
