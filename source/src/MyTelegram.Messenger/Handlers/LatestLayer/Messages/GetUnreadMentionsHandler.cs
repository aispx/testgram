namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get unread messages where we were mentioned
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getUnreadMentions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetUnreadMentionsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetUnreadMentions, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetUnreadMentions obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

        var limit = obj.Limit > 0 && obj.Limit <= 100 ? obj.Limit : 20;
        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesWithUnreadMentionsQuery(ownerPeerId, input.UserId, obj.OffsetId, limit, obj.MaxId, obj.MinId));

        var msgList = messages.Select(m => (IMessage)new TMessage
        {
            Id = m.MessageId,
            PeerId = peer.ToPeer(),
            Date = m.Date,
            Message = "",
            Mentioned = true,
        }).ToList();

        return new TMessages { Messages = new TVector<IMessage>(msgList), Topics = [], Chats = [], Users = [] };
    }
}
