using MongoDB.Bson;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// The <c>document</c> of a <c>wallPaper</c>, read out of <c>eventflow-documentreadmodel</c>.
///
/// <para>Shared by <c>account.getWallPaper</c>, <c>account.getWallPapers</c> and
/// <c>account.getMultiWallPapers</c>, which used to carry three copies of it and so three chances to
/// drift apart.</para>
/// </summary>
internal static class WallPaperDocumentReader
{
    public static MyTelegram.Schema.IDocument ToDocument(BsonDocument doc,
        IFileReferenceHelper fileReferenceHelper)
    {
        var documentId = doc["DocumentId"].AsInt64;

        return new MyTelegram.Schema.TDocument
        {
            Id = documentId,
            AccessHash = doc["AccessHash"].AsInt64,
            // Wallpaper rows are written by scripts/import_to_mongodb.py, which stores no FileReference
            // at all, so this used to be an empty reference — something the official server never
            // serves. See https://corefork.telegram.org/api/file-references
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = doc["Date"].AsInt32,
            MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "image/jpeg",
            Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
            Thumbs = new TVector<MyTelegram.Schema.IPhotoSize>(),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : MyTelegramConsts.MediaDcId,
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>()
        };
    }
}
