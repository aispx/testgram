using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Changes the main profile tab of a channel, see <a href="https://corefork.telegram.org/api/profile#tabs">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.setMainProfileTab"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 223: channels.setMainProfileTab#3583fcb1 channel:InputChannel tab:ProfileTab = Bool;
/// </remarks>
internal sealed class SetMainProfileTabHandler(
    IMongoDatabase database,
    IPeerHelper peerHelper,
    ILogger<SetMainProfileTabHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestSetMainProfileTab, IBool>, IObjectHandler
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestSetMainProfileTab obj)
    {
        // Extract channel ID
        long channelId = 0;
        if (obj.Channel is MyTelegram.Schema.TInputChannel inputChannel)
        {
            channelId = inputChannel.ChannelId;
        }
        else if (obj.Channel is MyTelegram.Schema.TInputChannelFromMessage inputChannelFromMessage)
        {
            channelId = inputChannelFromMessage.ChannelId;
        }

        if (channelId == 0)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        // Verify channel exists
        var collection = database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var channel = await collection.Find(filter).FirstOrDefaultAsync();

        if (channel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        // Serialize ProfileTab to string for storage
        var tabType = obj.Tab switch
        {
            MyTelegram.Schema.TProfileTabPosts => "Posts",
            MyTelegram.Schema.TProfileTabGifts => "Gifts",
            MyTelegram.Schema.TProfileTabMedia => "Media",
            MyTelegram.Schema.TProfileTabFiles => "Files",
            MyTelegram.Schema.TProfileTabMusic => "Music",
            MyTelegram.Schema.TProfileTabVoice => "Voice",
            MyTelegram.Schema.TProfileTabLinks => "Links",
            MyTelegram.Schema.TProfileTabGifs => "Gifs",
            _ => "Posts" // Default to Posts
        };

        // Update channel's main profile tab in MongoDB
        var update = Builders<BsonDocument>.Update.Set("MainProfileTab", tabType);
        await collection.UpdateOneAsync(filter, update);

        logger.LogInformation("Channel main profile tab updated: ChannelId={ChannelId}, Tab={Tab}",
            channelId, tabType);

        return new TBoolTrue();
    }
}