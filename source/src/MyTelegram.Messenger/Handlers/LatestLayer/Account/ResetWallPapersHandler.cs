using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Delete all installed wallpapers, reverting to the default wallpaper set.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.resetWallPapers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ResetWallPapersHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestResetWallPapers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestResetWallPapers obj)
    {
        // Remove all saved wallpapers
        var savedCol = database.GetCollection<BsonDocument>("user_wallpapers");
        await savedCol.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)
        );

        // Reset installed wallpaper
        var settingsCol = database.GetCollection<BsonDocument>("user_settings");
        var update = Builders<BsonDocument>.Update
            .Unset("InstalledWallpaperId")
            .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await settingsCol.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            update,
            new UpdateOptions { IsUpsert = true }
        );

        return new TBoolTrue();
    }
}
