using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Fetch new chats associated with an imported <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>. Must be invoked at most every <code>chatlist_update_period</code> seconds (as per the related <a href="https://corefork.telegram.org/api/config#chatlist-update-period">client configuration parameter »</a>).
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// 400 INPUT_CHATLIST_INVALID The specified folder is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.getChatlistUpdates"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para><c>missing_peers</c> is the link's current peer list minus what the folder already holds and minus
/// what the user dismissed with <c>chatlists.hideChatlistUpdates</c>. An answer that is always empty, which is
/// what this used to be, means a shared folder never picks up the chats its owner added later.</para>
/// </remarks>
internal sealed class GetChatlistUpdatesHandler(
    IChatlistUpdateResolver updateResolver,
    IChatlistPeerObjectsResolver peerObjectsResolver)
    : RpcResultObjectHandler<RequestGetChatlistUpdates, IChatlistUpdates>
{
    protected override async Task<IChatlistUpdates> HandleCoreAsync(IRequestInput input,
        RequestGetChatlistUpdates obj)
    {
        var info = await updateResolver.ResolveAsync(input.UserId, obj.Chatlist);
        var (chats, users) = await peerObjectsResolver.ResolveAsync(input, info.MissingPeers);

        return new TChatlistUpdates
        {
            MissingPeers = [.. info.MissingPeers.Select(p => p.ToPeer())],
            Chats = chats,
            Users = users
        };
    }
}
