using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Returns a list of available wallpapers.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getWallPapers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetWallPapersHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetWallPapers, MyTelegram.Schema.Account.IWallPapers>
{
    protected override async Task<MyTelegram.Schema.Account.IWallPapers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetWallPapers obj)
    {
        var collection = database.GetCollection<BsonDocument>("wallpapers");
        var wallpaperDocs = await collection.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();

        if (wallpaperDocs.Count == 0)
        {
            return new MyTelegram.Schema.Account.TWallPapersNotModified();
        }

        var hash = ComputeHash(wallpaperDocs);
        if (obj.Hash == hash)
        {
            return new MyTelegram.Schema.Account.TWallPapersNotModified();
        }

        var wallpapers = new TVector<MyTelegram.Schema.IWallPaper>();
        
        foreach (var doc in wallpaperDocs)
        {
            var documentId = doc.Contains("DocumentId") ? doc["DocumentId"].AsInt64 : 0;
            
            if (documentId == 0)
            {
                // WallPaperNoFile (gradient/solid)
                wallpapers.Add(new MyTelegram.Schema.TWallPaperNoFile
                {
                    Id = doc["WallpaperId"].AsInt64,
                    Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
                    Dark = doc.Contains("IsDark") && doc["IsDark"].AsBoolean,
                    Settings = ConvertSettings(doc)
                });
            }
            else
            {
                // WallPaper (image/pattern with document)
                var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
                var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", documentId);
                var docDoc = await docCollection.Find(docFilter).FirstOrDefaultAsync();
                
                if (docDoc != null)
                {
                    wallpapers.Add(new MyTelegram.Schema.TWallPaper
                    {
                        Id = doc["WallpaperId"].AsInt64,
                        AccessHash = doc["AccessHash"].AsInt64,
                        Slug = doc["Slug"].AsString,
                        Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
                        Pattern = doc.Contains("IsPattern") && doc["IsPattern"].AsBoolean,
                        Dark = doc.Contains("IsDark") && doc["IsDark"].AsBoolean,
                        Document = ConvertToDocument(docDoc),
                        Settings = ConvertSettings(doc)
                    });
                }
            }
        }

        return new MyTelegram.Schema.Account.TWallPapers
        {
            Hash = hash,
            Wallpapers = wallpapers
        };
    }

    private static long ComputeHash(IEnumerable<BsonDocument> docs)
    {
        var hash = new HashCode();
        foreach (var doc in docs.OrderBy(x => x["WallpaperId"].ToInt64()))
        {
            hash.Add(doc["WallpaperId"].ToInt64());
            hash.Add(doc.Contains("AccessHash") ? doc["AccessHash"].ToInt64() : 0);
            hash.Add(doc.Contains("DocumentId") ? doc["DocumentId"].ToInt64() : 0);
            hash.Add(doc.Contains("Slug") ? doc["Slug"].AsString : string.Empty);
        }

        return hash.ToHashCode();
    }

    private static MyTelegram.Schema.IWallPaperSettings? ConvertSettings(BsonDocument doc)
    {
        if (!doc.Contains("Settings") || doc["Settings"].IsBsonNull)
            return null;

        var settings = doc["Settings"].AsBsonDocument;

        // Check if settings has any actual values
        bool hasAnyValue = settings.Contains("Blur") || settings.Contains("Motion") ||
                          settings.Contains("BackgroundColor") || settings.Contains("SecondBackgroundColor") ||
                          settings.Contains("ThirdBackgroundColor") || settings.Contains("FourthBackgroundColor") ||
                          settings.Contains("Intensity") || settings.Contains("Rotation") || settings.Contains("Emoticon");

        if (!hasAnyValue)
            return null;

        var result = new MyTelegram.Schema.TWallPaperSettings
        {
            Blur = settings.Contains("Blur") && settings["Blur"].AsBoolean,
            Motion = settings.Contains("Motion") && settings["Motion"].AsBoolean,
            BackgroundColor = settings.Contains("BackgroundColor") ? settings["BackgroundColor"].AsInt32 : null,
            SecondBackgroundColor = settings.Contains("SecondBackgroundColor") ? settings["SecondBackgroundColor"].AsInt32 : null,
            ThirdBackgroundColor = settings.Contains("ThirdBackgroundColor") ? settings["ThirdBackgroundColor"].AsInt32 : null,
            FourthBackgroundColor = settings.Contains("FourthBackgroundColor") ? settings["FourthBackgroundColor"].AsInt32 : null,
            Intensity = settings.Contains("Intensity") ? settings["Intensity"].AsInt32 : null,
            Rotation = settings.Contains("Rotation") ? settings["Rotation"].AsInt32 : null,
            Emoticon = settings.Contains("Emoticon") ? settings["Emoticon"].AsString : null
        };

        return result;
    }

    private static MyTelegram.Schema.IDocument ConvertToDocument(BsonDocument doc)
    {
        var fileRef = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
            ? doc["FileReference"].AsBsonBinaryData.Bytes
            : Array.Empty<byte>();

        return new MyTelegram.Schema.TDocument
        {
            Id = doc["DocumentId"].AsInt64,
            AccessHash = doc["AccessHash"].AsInt64,
            FileReference = fileRef,
            Date = doc["Date"].AsInt32,
            MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "image/jpeg",
            Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
            Thumbs = new TVector<MyTelegram.Schema.IPhotoSize>(),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2,
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>()
        };
    }
}
