using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark messages as read in a <a href="https://corefork.telegram.org/api/monoforum">monoforum topic »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 PARENT_PEER_INVALID The specified <code>parent_peer</code> is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readSavedHistory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadSavedHistoryHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadSavedHistory, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadSavedHistory obj)
    {
        if (obj.MaxId > 0)
        {
            var parentPeer = peerHelper.GetPeer(obj.ParentPeer);
            if (parentPeer.PeerType == PeerType.Channel)
            {
                var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(parentPeer.PeerId));
                var peer = peerHelper.GetPeer(obj.Peer);
                if (monoforum == null || !monoforum.IsMonoforum || peer.PeerType != PeerType.User)
                {
                    RpcErrors.RpcErrors400.ParentPeerInvalid.ThrowRpcError();
                }

                var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(parentPeer.PeerId, obj.MaxId).Value));
                if (messageReadModel == null)
                {
                    RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
                }

                var stateMap = await MonoForumTopicStateHelper.GetStatesAsync(mongoDatabase, parentPeer.PeerId, input.UserId, [peer]);
                var state = stateMap.GetValueOrDefault(MonoForumTopicStateHelper.BuildId(parentPeer.PeerId, input.UserId, peer));
                if (MonoForumTopicStateHelper.GetReadInboxMaxId(state) >= obj.MaxId)
                {
                    return new TBoolTrue();
                }

                await MonoForumTopicStateHelper.UpsertReadInboxMaxIdAsync(mongoDatabase, parentPeer.PeerId, input.UserId, peer, obj.MaxId);

                var updates = new TUpdates
                {
                    Updates = new TVector<IUpdate>(new TUpdateReadMonoForumInbox
                    {
                        ChannelId = parentPeer.PeerId,
                        SavedPeerId = GetSavedDialogsHandler.ToSchemaPeer(peer),
                        ReadMaxId = obj.MaxId
                    }),
                    Users = new TVector<IUser>(),
                    Chats = new TVector<IChat>(),
                    Date = DateTime.UtcNow.ToTimestamp()
                };
                await objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), updates, input.AuthKeyId);
                return new TBoolTrue();
            }
        }

        return new TBoolTrue();
    }
}
