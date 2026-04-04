using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema.Users;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Get songs <a href="https://corefork.telegram.org/api/profile#music">pinned to the user's profile, see here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getSavedMusic"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedMusicHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IUserAppService userAppService,
    IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetSavedMusic, MyTelegram.Schema.Users.ISavedMusic>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Users.ISavedMusic> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetSavedMusic obj)
    {
        // Validate access hash
        await accessHashHelper.CheckAccessHashAsync(input, obj.Id);

        // Get target user
        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;

        // Verify user exists
        var userReadModel = await userAppService.GetAsync(targetUserId);
        if (userReadModel == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Query saved music from MongoDB
        var collection = database.GetCollection<BsonDocument>("saved_music");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", targetUserId);
        var sort = Builders<BsonDocument>.Sort.Ascending("Order"); // Sort by Order field for proper ordering

        var totalCount = (int)await collection.CountDocumentsAsync(filter);

        var docs = await collection.Find(filter)
            .Sort(sort)
            .Skip(obj.Offset)
            .Limit(obj.Limit)
            .ToListAsync();

        var documents = new TVector<MyTelegram.Schema.IDocument>();

        // Load documents from eventflow-documentreadmodel
        if (docs.Count > 0)
        {
            var documentIds = docs.Select(d => d["DocumentId"].AsInt64).ToList();
            var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
            var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", documentIds);
            var docDocs = await docCollection.Find(docFilter).ToListAsync();

            foreach (var docDoc in docDocs)
            {
                documents.Add(ConvertToDocument(docDoc));
            }
        }

        return new TSavedMusic
        {
            Count = totalCount,
            Documents = documents
        };
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
            MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "audio/mpeg",
            Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
            Thumbs = new TVector<MyTelegram.Schema.IPhotoSize>(),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2,
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>()
        };
    }
}