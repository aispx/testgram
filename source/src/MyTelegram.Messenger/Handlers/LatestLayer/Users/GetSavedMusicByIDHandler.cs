using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema.Users;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Check if the passed songs are still pinned to the user's profile, or refresh the file references of songs pinned on a user's profile <a href="https://corefork.telegram.org/api/profile#music">see here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getSavedMusicByID"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedMusicByIDHandler(
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IFileReferenceHelper fileReferenceHelper,
    IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetSavedMusicByID, MyTelegram.Schema.Users.ISavedMusic>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Users.ISavedMusic> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetSavedMusicByID obj)
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

        // Extract document IDs from InputDocument
        var documentIds = new List<long>();
        foreach (var inputDoc in obj.Documents)
        {
            if (inputDoc is MyTelegram.Schema.TInputDocument doc)
            {
                documentIds.Add(doc.Id);
            }
        }

        if (documentIds.Count == 0)
        {
            return new TSavedMusic
            {
                Count = 0,
                Documents = new TVector<MyTelegram.Schema.IDocument>()
            };
        }

        // Check if these documents are in saved_music for this user
        var savedMusicCol = database.GetCollection<BsonDocument>("saved_music");
        var savedFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", targetUserId),
            Builders<BsonDocument>.Filter.In("DocumentId", documentIds)
        );
        var savedDocs = await savedMusicCol.Find(savedFilter).ToListAsync();
        var savedDocIds = savedDocs.Select(d => d["DocumentId"].AsInt64).ToHashSet();

        // Load documents from eventflow-documentreadmodel
        var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", documentIds);
        var docDocs = await docCollection.Find(docFilter).ToListAsync();

        var documents = new TVector<MyTelegram.Schema.IDocument>();
        foreach (var docDoc in docDocs)
        {
            var docId = docDoc["DocumentId"].AsInt64;
            // Only include if it's in saved_music
            if (savedDocIds.Contains(docId))
            {
                documents.Add(ConvertToDocument(docDoc));
            }
        }

        return new TSavedMusic
        {
            Count = documents.Count,
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