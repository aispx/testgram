using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Delete message history of a <a href="https://corefork.telegram.org/api/forum">forum topic</a>
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 TOPIC_ID_INVALID The specified topic ID is invalid.
/// See <a href="https://corefork.telegram.org/method/channels.deleteTopicHistory" />
///</summary>
internal sealed class DeleteTopicHistoryHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestDeleteTopicHistory, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestDeleteTopicHistory obj)
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

        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.DeleteMessages))
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();

        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var topicFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", obj.TopMsgId)
        );

        var topic = await topicsCol.Find(topicFilter).FirstOrDefaultAsync();
        if (topic == null)
            RpcErrors.RpcErrors400.TopicIdInvalid.ThrowRpcError();

        var messageCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var messageFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerPeerId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", obj.TopMsgId)
        );

        var messages = await messageCol.Find(messageFilter)
            .Project(Builders<BsonDocument>.Projection.Include("MessageId"))
            .ToListAsync();

        var messageIds = messages.Select(m => m["MessageId"].AsInt32).ToList();

        if (messageIds.Count > 0)
        {
            var newTopMessageId = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(channelId, messageIds));
            var command = new StartDeleteChannelMessagesCommand(
                TempId.New,
                input.ToRequestInfo(),
                channelId,
                messageIds,
                newTopMessageId,
                null,
                null,
                null
            );
            await commandBus.PublishAsync(command);
        }

        return new TAffectedHistory
        {
            Pts = 0,
            PtsCount = messageIds.Count,
            Offset = 0
        };
    }
}
