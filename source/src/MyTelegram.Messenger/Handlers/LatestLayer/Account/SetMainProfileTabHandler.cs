using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Changes the main profile tab of the current user, see <a href="https://corefork.telegram.org/api/profile#tabs">here »</a> for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.setMainProfileTab"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 223: account.setMainProfileTab#5dee78b0 tab:ProfileTab = Bool;
/// </remarks>
internal sealed class SetMainProfileTabHandler(
    IMongoDatabase database,
    ILogger<SetMainProfileTabHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSetMainProfileTab, IBool>, IObjectHandler
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSetMainProfileTab obj)
    {
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

        // Update user's main profile tab in MongoDB
        var collection = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var update = Builders<BsonDocument>.Update.Set("MainProfileTab", tabType);

        await collection.UpdateOneAsync(filter, update);

        logger.LogInformation("Main profile tab updated: UserId={UserId}, Tab={Tab}",
            input.UserId, tabType);

        return new TBoolTrue();
    }
}