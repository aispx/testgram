using MyTelegram.Messenger.Services.Scheduled;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Delete scheduled messages
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 403 MESSAGE_DELETE_FORBIDDEN You can't delete one of the messages you tried to delete, most likely because it is a service message.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteScheduledMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteScheduledMessagesHandler(
    IPeerHelper peerHelper,
    IScheduledMessageStore scheduledMessageStore,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteScheduledMessages, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestDeleteScheduledMessages obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var sharedQueue = await scheduledMessageStore.CheckQueueAccessAsync(peer, input.UserId);
        var documents = await scheduledMessageStore.GetQueueAsync(peer, input.UserId, sharedQueue, obj.Id.ToList());

        // Deleting an entry that already left the queue (it fired a moment ago, or another session
        // removed it) is not an error: the client only has to learn that it is gone.
        await scheduledMessageStore.DeleteAsync(documents.Select(p => p.Id));

        var updates = scheduledMessageStore.BuildDeleteScheduledUpdates(peer, obj.Id.ToList());

        // The requesting session gets the update as the rpc result, the other ones by push.
        foreach (var senderUserId in documents.Select(p => p.SenderUserId).Distinct())
        {
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, senderUserId), updates,
                excludeAuthKeyId: senderUserId == input.UserId ? input.AuthKeyId : null);
        }

        return updates;
    }
}
