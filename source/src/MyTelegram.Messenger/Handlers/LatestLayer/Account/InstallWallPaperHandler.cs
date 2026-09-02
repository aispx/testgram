using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Install wallpaper
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.installWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>"When a client sets a wallpaper as the default chat background, call account.installWallPaper to
/// signal this installation to the server. Note that calling this method will also automatically save the
/// wallpaper, if it's not present in the saved wallpapers list."</para>
///
/// <para>The auto-save is the whole of the observable behaviour. <c>InstalledWallpaperId</c> below is
/// deliberately written and never read: no method in the API reports the installed wallpaper back, so
/// there is nothing to serve it to — the record exists because the method's stated purpose is to signal
/// the installation. Do not go looking for the consumer.</para>
/// </remarks>
internal sealed class InstallWallPaperHandler(
    IMongoDatabase database,
    IWallPaperCatalog catalog,
    IUserWallPaperStore userWallPaperStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestInstallWallPaper, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestInstallWallPaper obj)
    {
        var row = await WallPaperInputResolver.ResolveInstallableAsync(catalog, obj.Wallpaper);

        if (!await userWallPaperStore.IsSavedAsync(input.UserId, row.WallPaperId))
        {
            await userWallPaperStore.SaveAsync(input.UserId, row, obj.Settings);
        }

        await database.GetCollection<BsonDocument>("user_settings").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Update
                .Set("InstalledWallpaperId", row.WallPaperId)
                .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
