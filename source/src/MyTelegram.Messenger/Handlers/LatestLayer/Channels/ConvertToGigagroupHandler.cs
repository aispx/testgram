using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Convert a <a href="https://corefork.telegram.org/api/channel">supergroup</a> to a <a href="https://corefork.telegram.org/api/channel">gigagroup</a>, when requested by <a href="https://corefork.telegram.org/api/config#channel-suggestions">channel suggestions</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_ID_INVALID The specified supergroup ID is invalid.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 FORUM_ENABLED You can't execute the specified action because the group is a <a href="https://corefork.telegram.org/api/forum">forum</a>, disable forum functionality to continue.
/// 400 PARTICIPANTS_TOO_FEW Not enough participants.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.convertToGigagroup"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ConvertToGigagroupHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<RequestConvertToGigagroup, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestConvertToGigagroup obj)
    {
        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Channel, input.UserId);

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
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (channelDoc.Contains("Broadcast") && channelDoc["Broadcast"].AsBoolean)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (channelDoc.Contains("Forum") && channelDoc["Forum"].AsBoolean)
            RpcErrors.RpcErrors400.ForumEnabled.ThrowRpcError();

        var participantsCount = channelDoc.Contains("ParticipantsCount") ? channelDoc["ParticipantsCount"].AsInt32 : 0;
        if (participantsCount < 200)
            RpcErrors.RpcErrors400.ParticipantsTooFew.ThrowRpcError();

        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = Builders<BsonDocument>.Update.Set("Gigagroup", true);
        await collection.UpdateOneAsync(filter, update);

        return new TUpdates { Updates = new TVector<IUpdate>(), Chats = new TVector<IChat>(), Users = new TVector<IUser>(), Date = CurrentDate };
    }
}