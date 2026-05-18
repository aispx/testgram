using MongoDB.Bson;
using MongoDB.Driver;

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
        BsonValue emojiStatusValue = obj.EmojiStatus switch
        {
            TEmojiStatus status => new BsonDocument
            {
                ["DocumentId"] = status.DocumentId,
                ["Until"] = status.Until.HasValue ? new BsonInt32(status.Until.Value) : BsonNull.Value,
            },
            TEmojiStatusEmpty => BsonNull.Value,
            _ => BsonNull.Value,
        };

        if (obj.EmojiStatus is not TEmojiStatus and not TEmojiStatusEmpty)
            RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();

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
