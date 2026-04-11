using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Can only be invoked by non-bot admins of a <a href="https://corefork.telegram.org/api/monoforum">monoforum »</a>, obtains the original sender of a message sent by other monoforum admins to the monoforum, on behalf of the channel associated to the monoforum.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getMessageAuthor"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMessageAuthorHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetMessageAuthor, MyTelegram.Schema.IUser>
{
    protected override async Task<MyTelegram.Schema.IUser> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestGetMessageAuthor obj)
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

        var channelCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null || !channelDoc.Contains("IsMonoforum") || !channelDoc["IsMonoforum"].AsBoolean)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => true))
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var messageCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var messageDoc = await messageCol.Find(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", channelId),
                Builders<BsonDocument>.Filter.Eq("MessageId", obj.Id)
            )
        ).FirstOrDefaultAsync();

        if (messageDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var senderUserId = messageDoc.Contains("SenderUserId") ? messageDoc["SenderUserId"].AsInt64 : 0;
        if (senderUserId == 0)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var user = await userConverterService.GetUserAsync(input, senderUserId, false, false, input.Layer);
        return user;
    }
}