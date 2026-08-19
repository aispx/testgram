using MyTelegram.Messenger.Services.Scheduled;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get scheduled messages
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getScheduledHistory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetScheduledHistoryHandler(
    IPeerHelper peerHelper,
    IScheduledMessageStore scheduledMessageStore,
    IScheduledMessagesResponseBuilder responseBuilder)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetScheduledHistory,
        MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetScheduledHistory obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var sharedQueue = await scheduledMessageStore.CheckQueueAccessAsync(peer, input.UserId);
        var documents = await scheduledMessageStore.GetQueueAsync(peer, input.UserId, sharedQueue);

        return await responseBuilder.ToMessagesAsync(input, peer, documents, obj.Hash);
    }
}
