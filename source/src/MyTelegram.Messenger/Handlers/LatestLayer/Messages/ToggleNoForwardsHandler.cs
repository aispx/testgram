using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Enable or disable <a href="https://telegram.org/blog/protected-content-delete-by-date-and-more">content protection</a> on a channel or chat
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleNoForwards"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleNoForwardsHandler(
    ICommandBus commandBus,
    IAccessHashHelper accessHashHelper,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<RequestToggleNoForwards, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestToggleNoForwards obj)
    {
        switch (obj.Peer)
        {
            case TInputPeerChannel inputPeerChannel:
                await accessHashHelper.CheckAccessHashAsync(input, inputPeerChannel.ChannelId, inputPeerChannel.AccessHash, AccessHashType.Channel);
            {
                var command = new ToggleChannelNoForwardsCommand(ChannelId.Create(inputPeerChannel.ChannelId), input.ToRequestInfo(), obj.Enabled);
                await commandBus.PublishAsync(command);
            }

                return null !;
            case TInputPeerChat inputPeerChat:
            {
                var command = new ToggleChannelNoForwardsCommand(ChannelId.Create(inputPeerChat.ChatId), input.ToRequestInfo(), obj.Enabled);
                await commandBus.PublishAsync(command);
                return null!;
            }
            case TInputPeerUser inputPeerUser:
                await accessHashHelper.CheckAccessHashAsync(input, inputPeerUser.UserId, inputPeerUser.AccessHash, AccessHashType.User);
                return await TogglePrivateChatNoForwardsAsync(input, inputPeerUser, obj.Enabled);
        }

        RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        return null!;
    }

    private async Task<IUpdates> TogglePrivateChatNoForwardsAsync(
        IRequestInput input,
        TInputPeerUser inputPeerUser,
        bool enabled)
    {
        var peer = peerHelper.GetPeer(inputPeerUser, input.UserId);
        var collection = mongoDatabase.GetCollection<PrivateChatNoForwardsDocument>("private_chat_noforwards");
        var filter = Builders<PrivateChatNoForwardsDocument>.Filter.And(
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.OwnerUserId, input.UserId),
            Builders<PrivateChatNoForwardsDocument>.Filter.Eq(x => x.PeerUserId, peer.PeerId));
        var current = await collection.Find(filter).FirstOrDefaultAsync();
        var previousValue = current?.Enabled == true;

        if (previousValue == enabled)
        {
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();
        }

        var update = Builders<PrivateChatNoForwardsDocument>.Update
            .Set(x => x.OwnerUserId, input.UserId)
            .Set(x => x.PeerUserId, peer.PeerId)
            .Set(x => x.Enabled, enabled)
            .Set(x => x.UpdatedAt, CurrentDate);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            peer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: new TMessageActionNoForwardsToggle
            {
                // The persisted setting stores "content protection enabled", while the
                // service action text is rendered from the opposite sharing state.
                PrevValue = !previousValue,
                NewValue = !enabled
            });
        await messageAppService.SendMessageAsync([sendInput]);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}
