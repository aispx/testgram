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
    IUserAppService userAppService,
    IPrivacyAppService privacyAppService,
    IFileReferenceHelper fileReferenceHelper,
    IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetSavedMusic, MyTelegram.Schema.Users.ISavedMusic>, IObjectHandler
{
    private const int MaxLimit = 100;
    private const int DefaultLimit = 20;

    protected override async Task<MyTelegram.Schema.Users.ISavedMusic> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetSavedMusic obj)
    {
        // Validate access hash

        // Get target user
        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;

        // Verify user exists
        var userReadModel = await userAppService.GetAsync(targetUserId);
        if (userReadModel == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // privacyKeySavedMusic hides the profile playlist from disallowed viewers. An empty
        // result is the documented outcome here — the official server does not raise an error.
        if (targetUserId != input.UserId)
        {
            var allowed = true;
            await privacyAppService.ApplyPrivacyAsync(input.UserId, targetUserId,
                _ => allowed = false,
                [PrivacyType.SavedMusic]);

            if (!allowed)
            {
                return new TSavedMusic
                {
                    Count = 0,
                    Documents = new TVector<MyTelegram.Schema.IDocument>()
                };
            }
        }

        // Query saved music from MongoDB
        var collection = database.GetCollection<BsonDocument>("saved_music");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", targetUserId);
        var sort = Builders<BsonDocument>.Sort.Ascending("Order"); // Sort by Order field for proper ordering

        var totalCount = (int)await collection.CountDocumentsAsync(filter);

        // A negative Skip is accepted by the driver but rejected by the MongoDB server, which
        // surfaced as INTERNAL_ERROR; Limit(0) is not treated as "no limit" by IFindFluent, so an
        // unclamped zero returned the target's whole saved-music list.
        var offset = Math.Max(0, obj.Offset);
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;

        var docs = await collection.Find(filter)
            .Sort(sort)
            .Skip(offset)
            .Limit(limit)
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

    private MyTelegram.Schema.IDocument ConvertToDocument(BsonDocument doc)
    {
        var attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>();

        // Use Attributes2 if available (proper serialization)
        if (doc.Contains("Attributes2") && !doc["Attributes2"].IsBsonNull)
        {
            var attrs2 = doc["Attributes2"].AsBsonArray;
            foreach (var attrBson in attrs2)
            {
                if (attrBson.IsBsonDocument)
                {
                    var attrDoc = attrBson.AsBsonDocument;
                    var typeName = attrDoc["_t"].AsString;

                    // Handle audio attributes
                    if (typeName.EndsWith("TDocumentAttributeAudio"))
                    {
                        var audioAttr = new MyTelegram.Schema.TDocumentAttributeAudio
                        {
                            Voice = attrDoc.Contains("Voice") && attrDoc["Voice"].AsBoolean,
                            Duration = attrDoc.Contains("Duration") ? attrDoc["Duration"].AsInt32 : 0,
                            Title = attrDoc.Contains("Title") ? attrDoc["Title"].AsString : null,
                            Performer = attrDoc.Contains("Performer") ? attrDoc["Performer"].AsString : null,
                            Waveform = attrDoc.Contains("Waveform") && !attrDoc["Waveform"].IsBsonNull
                                ? attrDoc["Waveform"].AsByteArray : null
                        };
                        attributes.Add(audioAttr);
                    }
                    // Handle filename attributes
                    else if (typeName.EndsWith("TDocumentAttributeFilename"))
                    {
                        attributes.Add(new MyTelegram.Schema.TDocumentAttributeFilename
                        {
                            FileName = attrDoc.Contains("FileName") ? attrDoc["FileName"].AsString : ""
                        });
                    }
                }
            }
        }

        var documentId = doc["DocumentId"].AsInt64;

        return new MyTelegram.Schema.TDocument
        {
            Id = documentId,
            AccessHash = doc["AccessHash"].AsInt64,
            // See https://corefork.telegram.org/api/file-references
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = doc["Date"].AsInt32,
            MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "audio/mpeg",
            Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
            Thumbs = new TVector<MyTelegram.Schema.IPhotoSize>(),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2,
            Attributes = attributes
        };
    }
}