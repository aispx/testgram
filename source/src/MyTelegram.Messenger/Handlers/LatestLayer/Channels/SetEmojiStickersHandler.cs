using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Set a <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickerset</a> for supergroups. Only usable after reaching at least the <a href="https://corefork.telegram.org/api/boost">boost level »</a> specified in the <a href="https://corefork.telegram.org/api/config#group-emoji-stickers-level-min"><code>group_emoji_stickers_level_min</code> »</a> config parameter.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.setEmojiStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetEmojiStickersHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestSetEmojiStickers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestSetEmojiStickers obj)
    {
        await channelAdminRightsChecker.CheckAdminRightAsync(obj.Channel, input.UserId, p => p.ChangeInfo);
        // Convert IInputChannel to IInputPeer
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

        // Get stickerset ID
        long? stickersetId = null;
        if (obj.Stickerset is TInputStickerSetID inputSet)
        {
            stickersetId = inputSet.Id;
        }
        else if (obj.Stickerset is TInputStickerSetEmpty)
        {
            stickersetId = null;
        }

        // Update MongoDB
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = stickersetId.HasValue
            ? Builders<BsonDocument>.Update.Set("EmojiSet", stickersetId.Value)
            : Builders<BsonDocument>.Update.Unset("EmojiSet");
        await collection.UpdateOneAsync(filter, update);

        return new MyTelegram.Schema.TBoolTrue();
    }
}