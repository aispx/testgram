using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Set an <a href="https://corefork.telegram.org/api/emoji-status">emoji status</a> for a channel or supergroup.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.updateEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateEmojiStatusHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdateEmojiStatus, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestUpdateEmojiStatus obj)
    {
        var channelId = obj.Channel switch
        {
            TInputChannel channel => channel.ChannelId,
            TInputChannelFromMessage channelFromMessage => channelFromMessage.ChannelId,
            _ => 0,
        };
        if (channelId == 0)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        BsonValue emojiStatusValue;
        switch (obj.EmojiStatus)
        {
            case TEmojiStatus status:
                emojiStatusValue = new BsonDocument
                {
                    ["DocumentId"] = status.DocumentId,
                    ["Until"] = status.Until.HasValue ? new BsonInt32(status.Until.Value) : BsonNull.Value,
                };
                break;
            case TInputEmojiStatusCollectible collectible:
            {
                var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .Find(d => d.UniqueId == collectible.CollectibleId && !d.Burned)
                    .FirstOrDefaultAsync();
                if (doc == null)
                    RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                var model = doc.Attributes.FirstOrDefault(a => a.Type == "model");
                var documentId = model?.DocumentId ?? doc.DocumentId;
                emojiStatusValue = new BsonDocument
                {
                    ["DocumentId"] = documentId,
                    ["Until"] = collectible.Until.HasValue ? new BsonInt32(collectible.Until.Value) : BsonNull.Value,
                };
                break;
            }
            case TEmojiStatusEmpty:
                emojiStatusValue = BsonNull.Value;
                break;
            default:
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                return null!;
        }

        await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
                Builders<BsonDocument>.Update.Set("EmojiStatus", emojiStatusValue));

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
