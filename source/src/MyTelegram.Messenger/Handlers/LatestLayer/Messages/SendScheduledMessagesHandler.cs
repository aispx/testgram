using MyTelegram.Messenger.Services.Scheduled;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Send scheduled messages right away
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendScheduledMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendScheduledMessagesHandler(
    IPeerHelper peerHelper,
    IScheduledMessageStore scheduledMessageStore,
    IScheduledMessageDispatcher scheduledMessageDispatcher)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendScheduledMessages, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestSendScheduledMessages obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var sharedQueue = await scheduledMessageStore.CheckQueueAccessAsync(peer, input.UserId);
        var documents = await scheduledMessageStore.GetQueueAsync(peer, input.UserId, sharedQueue, obj.Id.ToList());
        if (documents.Count == 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // The messages themselves are delivered by the send pipeline (updateNewMessage with the
        // from_scheduled flag); this answer only reports that the queue was flushed.
        return await scheduledMessageDispatcher.FlushAsync(documents, input.ToRequestInfo());
    }
}
