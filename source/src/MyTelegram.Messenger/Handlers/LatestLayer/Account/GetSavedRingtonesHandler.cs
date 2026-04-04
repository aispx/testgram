using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Fetch saved notification sounds
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getSavedRingtones"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedRingtonesHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetSavedRingtones, MyTelegram.Schema.Account.ISavedRingtones>
{
    protected override async Task<MyTelegram.Schema.Account.ISavedRingtones> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetSavedRingtones obj)
    {
        var collection = database.GetCollection<BsonDocument>("saved_ringtones");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var sort = Builders<BsonDocument>.Sort.Descending("SavedAt");
        
        var savedDocs = await collection.Find(filter).Sort(sort).ToListAsync();

        if (savedDocs.Count == 0)
        {
            return new MyTelegram.Schema.Account.TSavedRingtones
            {
                Hash = obj.Hash,
                Ringtones = new TVector<MyTelegram.Schema.IDocument>()
            };
        }

        var documentIds = savedDocs.Select(d => d["DocumentId"].AsInt64).ToList();

        // Load documents
        var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.In("DocumentId", documentIds);
        var docDocs = await docCollection.Find(docFilter).ToListAsync();

        var ringtones = new TVector<MyTelegram.Schema.IDocument>();
        
        foreach (var docDoc in docDocs)
        {
            ringtones.Add(ConvertToDocument(docDoc));
        }

        return new MyTelegram.Schema.Account.TSavedRingtones
        {
            Hash = obj.Hash,
            Ringtones = ringtones
        };
    }

    private static MyTelegram.Schema.IDocument ConvertToDocument(BsonDocument doc)
    {
        var fileRef = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
            ? doc["FileReference"].AsBsonBinaryData.Bytes
            : Array.Empty<byte>();

        var attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>();

        if (doc.Contains("Attributes2") && !doc["Attributes2"].IsBsonNull)
        {
            var attrs2 = doc["Attributes2"].AsBsonArray;
            foreach (var attrBson in attrs2)
            {
                if (attrBson.IsBsonDocument)
                {
                    var attrDoc = attrBson.AsBsonDocument;
                    var typeName = attrDoc["_t"].AsString;

                    if (typeName.EndsWith("TDocumentAttributeAudio"))
                    {
                        attributes.Add(new MyTelegram.Schema.TDocumentAttributeAudio
                        {
                            Voice = attrDoc.Contains("Voice") && attrDoc["Voice"].AsBoolean,
                            Duration = attrDoc.Contains("Duration") ? attrDoc["Duration"].AsInt32 : 0,
                            Title = attrDoc.Contains("Title") ? attrDoc["Title"].AsString : null,
                            Performer = attrDoc.Contains("Performer") ? attrDoc["Performer"].AsString : null,
                            Waveform = attrDoc.Contains("Waveform") && !attrDoc["Waveform"].IsBsonNull
                                ? attrDoc["Waveform"].AsByteArray : null
                        });
                    }
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
            Attributes = attributes
        };
    }
}
