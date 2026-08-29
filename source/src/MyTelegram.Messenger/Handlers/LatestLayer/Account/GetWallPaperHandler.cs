using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get info about a certain wallpaper
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetWallPaperHandler(IMongoDatabase database, IFileReferenceHelper fileReferenceHelper) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetWallPaper, MyTelegram.Schema.IWallPaper>
{
    protected override async Task<MyTelegram.Schema.IWallPaper> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetWallPaper obj)
    {
        long wallpaperId = 0;
        string? slug = null;

        if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaper inputWallpaper)
        {
            wallpaperId = inputWallpaper.Id;
        }
        else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperSlug inputSlug)
        {
            slug = inputSlug.Slug;
        }
        else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperNoFile inputNoFile)
        {
            wallpaperId = inputNoFile.Id;
        }

        var collection = database.GetCollection<BsonDocument>("wallpapers");
        FilterDefinition<BsonDocument> filter;

        if (!string.IsNullOrEmpty(slug))
        {
            filter = Builders<BsonDocument>.Filter.Eq("Slug", slug);
        }
        else if (wallpaperId != 0)
        {
            filter = Builders<BsonDocument>.Filter.Eq("WallpaperId", wallpaperId);
        }
        else
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
            return null!;
        }

        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        if (doc == null)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        var documentId = doc.Contains("DocumentId") ? doc["DocumentId"].AsInt64 : 0;

        if (documentId == 0)
        {
            return new MyTelegram.Schema.TWallPaperNoFile
            {
                Id = doc["WallpaperId"].AsInt64,
                Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
                Dark = doc.Contains("IsDark") && doc["IsDark"].AsBoolean,
                Settings = ConvertSettings(doc)
            };
        }

        var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", documentId);
        var docDoc = await docCollection.Find(docFilter).FirstOrDefaultAsync();

        if (docDoc == null)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        return new MyTelegram.Schema.TWallPaper
        {
            Id = doc["WallpaperId"].AsInt64,
            AccessHash = doc["AccessHash"].AsInt64,
            Slug = doc["Slug"].AsString,
            Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
            Pattern = doc.Contains("IsPattern") && doc["IsPattern"].AsBoolean,
            Dark = doc.Contains("IsDark") && doc["IsDark"].AsBoolean,
            Document = WallPaperDocumentReader.ToDocument(docDoc, fileReferenceHelper),
            Settings = ConvertSettings(doc)
        };
    }

    private static MyTelegram.Schema.IWallPaperSettings? ConvertSettings(BsonDocument doc)
    {
        if (!doc.Contains("Settings") || doc["Settings"].IsBsonNull)
            return null;

        var settings = doc["Settings"].AsBsonDocument;
        var result = new MyTelegram.Schema.TWallPaperSettings();

        if (settings.Contains("Blur"))
            result.Blur = settings["Blur"].AsBoolean;
        
        if (settings.Contains("Motion"))
            result.Motion = settings["Motion"].AsBoolean;
        
        if (settings.Contains("BackgroundColor"))
            result.BackgroundColor = settings["BackgroundColor"].AsInt32;
        
        if (settings.Contains("SecondBackgroundColor"))
            result.SecondBackgroundColor = settings["SecondBackgroundColor"].AsInt32;
        
        if (settings.Contains("ThirdBackgroundColor"))
            result.ThirdBackgroundColor = settings["ThirdBackgroundColor"].AsInt32;
        
        if (settings.Contains("FourthBackgroundColor"))
            result.FourthBackgroundColor = settings["FourthBackgroundColor"].AsInt32;
        
        if (settings.Contains("Intensity"))
            result.Intensity = settings["Intensity"].AsInt32;
        
        if (settings.Contains("Rotation"))
            result.Rotation = settings["Rotation"].AsInt32;
        
        if (settings.Contains("Emoticon"))
            result.Emoticon = settings["Emoticon"].AsString;

        return WallPaperSettingsHelper.PairSharedFlags(result);
    }
}
