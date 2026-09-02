using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Delete all installed wallpapers, reverting to the default wallpaper set.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.resetWallPapers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>"To restore the default list, removing all installed wallpapers and reinstalling previously
/// removed preinstalled wallpapers." Both halves matter: dropping the saved rows is not enough, the
/// tombstones left by <c>saveWallPaper(unsave: true)</c> have to go too, or the preinstalled wallpapers
/// the user removed stay removed.</para>
/// </remarks>
internal sealed class ResetWallPapersHandler(IMongoDatabase database, IUserWallPaperStore userWallPaperStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestResetWallPapers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestResetWallPapers obj)
    {
        await userWallPaperStore.ResetAsync(input.UserId);

        await database.GetCollection<BsonDocument>("user_settings").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Update
                .Unset("InstalledWallpaperId")
                .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
