using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get info about multiple wallpapers
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getMultiWallPapers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMultiWallPapersHandler(IMongoDatabase database, IFileReferenceHelper fileReferenceHelper) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetMultiWallPapers, TVector<MyTelegram.Schema.IWallPaper>>
{
    protected override async Task<TVector<MyTelegram.Schema.IWallPaper>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetMultiWallPapers obj)
    {
        var wallpaperIds = new List<long>();
        var slugs = new List<string>();

        // Extract IDs and slugs
        foreach (var inputWallpaper in obj.Wallpapers)
        {
            if (inputWallpaper is MyTelegram.Schema.TInputWallPaper wp)
            {
                wallpaperIds.Add(wp.Id);
            }
            else if (inputWallpaper is MyTelegram.Schema.TInputWallPaperSlug wpSlug)
            {
                slugs.Add(wpSlug.Slug);
            }
            else if (inputWallpaper is MyTelegram.Schema.TInputWallPaperNoFile wpNoFile)
            {
                wallpaperIds.Add(wpNoFile.Id);
            }
        }

        var collection = database.GetCollection<BsonDocument>("wallpapers");
        var filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.In("WallpaperId", wallpaperIds),
            Builders<BsonDocument>.Filter.In("Slug", slugs)
        );

        var wallpaperDocs = await collection.Find(filter).ToListAsync();
        var result = new TVector<MyTelegram.Schema.IWallPaper>();

        foreach (var doc in wallpaperDocs)
        {
            var documentId = doc.Contains("DocumentId") ? doc["DocumentId"].AsInt64 : 0;
            
            if (documentId == 0)
            {
                result.Add(new MyTelegram.Schema.TWallPaperNoFile
                {
                    Id = doc["WallpaperId"].AsInt64,
                    Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
                    Dark = doc.Contains("IsDark") && doc["IsDark"].AsBoolean,
                    Settings = ConvertSettings(doc)
                });
            }
            else
            {
                var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
                var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", documentId);
                var docDoc = await docCollection.Find(docFilter).FirstOrDefaultAsync();
                
                if (docDoc != null)
                {
                    result.Add(new MyTelegram.Schema.TWallPaper
                    {
                        Id = doc["WallpaperId"].AsInt64,
                        AccessHash = doc["AccessHash"].AsInt64,
                        Slug = doc["Slug"].AsString,
                        Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
                        Pattern = doc.Contains("IsPattern") && doc["IsPattern"].AsBoolean,
                        Dark = doc.Contains("IsDark") && doc["IsDark"].AsBoolean,
                        Document = WallPaperDocumentReader.ToDocument(docDoc, fileReferenceHelper),
                        Settings = ConvertSettings(doc)
                    });
                }
            }
        }

        return result;
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
