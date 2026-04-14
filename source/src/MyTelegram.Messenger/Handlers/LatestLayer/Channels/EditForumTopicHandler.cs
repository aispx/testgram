using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Edit <a href="https://corefork.telegram.org/api/forum">forum topic</a>; requires <a href="https://corefork.telegram.org/api/rights"><code>manage_topics</code> rights</a>.
/// <para>Possible errors</para>
/// Code Type Description
/// 400 TOPIC_ID_INVALID The specified topic ID is invalid.
/// 400 TOPIC_NOT_MODIFIED The updated topic info is equal to the current topic info, nothing was changed.
/// See <a href="https://corefork.telegram.org/method/channels.editForumTopic" />
///</summary>
internal sealed class EditForumTopicHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestEditForumTopic, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestEditForumTopic obj)
    {
        IInputPeer inputPeer;
        if (obj.Channel is TInputChannel inputChannel)
        {
            inputPeer = new TInputPeerChannel { ChannelId = inputChannel.ChannelId, AccessHash = inputChannel.AccessHash };
        }
        else if (obj.Channel is TInputChannelFromMessage inputChannelFromMessage)
        {
            inputPeer = new TInputPeerChannelFromMessage
            {
                Peer = inputChannelFromMessage.Peer,
                MsgId = inputChannelFromMessage.MsgId,
                ChannelId = inputChannelFromMessage.ChannelId
            };
        }
        else
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.ManageTopics))
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();

        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", obj.TopicId)
        );

        var topic = await topicsCol.Find(filter).FirstOrDefaultAsync();
        if (topic == null)
            RpcErrors.RpcErrors400.TopicIdInvalid.ThrowRpcError();

        var currentTitle = topic.Contains("Title") ? topic["Title"].AsString : "";
        var currentIconEmojiId = topic.Contains("IconEmojiId") ? topic["IconEmojiId"].AsInt64 : 0L;
        var currentClosed = topic.Contains("Closed") ? topic["Closed"].AsBoolean : false;
        var currentHidden = topic.Contains("Hidden") ? topic["Hidden"].AsBoolean : false;

        var newTitle = obj.Title ?? currentTitle;
        var newIconEmojiId = obj.IconEmojiId ?? currentIconEmojiId;
        var newClosed = obj.Closed ?? currentClosed;
        var newHidden = obj.Hidden ?? currentHidden;

        if (newTitle == currentTitle && newIconEmojiId == currentIconEmojiId &&
            newClosed == currentClosed && newHidden == currentHidden)
            RpcErrors.RpcErrors400.TopicNotModified.ThrowRpcError();

        var update = Builders<BsonDocument>.Update
            .Set("Title", newTitle)
            .Set("IconEmojiId", newIconEmojiId)
            .Set("Closed", newClosed)
            .Set("Hidden", newHidden);

        await topicsCol.UpdateOneAsync(filter, update);

        // Send service message for topic edit
        var action = new TMessageActionTopicEdit();

        if (newTitle != currentTitle)
            action.Title = newTitle;

        if (newIconEmojiId != currentIconEmojiId)
            action.IconEmojiId = newIconEmojiId;

        if (newClosed != currentClosed)
            action.Closed = newClosed;

        if (newHidden != currentHidden)
            action.Hidden = newHidden;

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.Channel, channelId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action,
            replyToMsgId: obj.TopicId
        );

        await messageAppService.SendMessageAsync([sendInput]);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }
}
