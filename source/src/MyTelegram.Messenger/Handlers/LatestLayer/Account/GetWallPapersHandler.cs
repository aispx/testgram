using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Returns a list of available wallpapers.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getWallPapers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The list is <b>per account</b>: the preinstalled set plus whatever the caller saved, minus
/// whatever they removed. It used to be the whole <c>wallpapers</c> collection for everybody, which made
/// <c>saveWallPaper(unsave: true)</c> and <c>resetWallPapers</c> no-ops as far as any client could
/// tell.</para>
///
/// <para>The <c>hash</c> is the client's, computed by the client — see
/// <see cref="WallPaperListHashHelper"/>.</para>
/// </remarks>
internal sealed class GetWallPapersHandler(IUserWallPaperStore userWallPaperStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetWallPapers, MyTelegram.Schema.Account.IWallPapers>
{
    protected override async Task<MyTelegram.Schema.Account.IWallPapers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetWallPapers obj)
    {
        var wallPapers = await userWallPaperStore.GetListAsync(input.UserId);
        var hash = WallPaperListHashHelper.ComputeHash(wallPapers);

        // Zero is what a client sends when it has nothing cached, so it can never be a match — and an
        // empty list must come back as an empty list rather than as wallPapersNotModified, or a client
        // holding a stale copy would keep it forever.
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Account.TWallPapersNotModified();
        }

        return new MyTelegram.Schema.Account.TWallPapers
        {
            Hash = hash,
            Wallpapers = new TVector<MyTelegram.Schema.IWallPaper>(wallPapers)
        };
    }
}
