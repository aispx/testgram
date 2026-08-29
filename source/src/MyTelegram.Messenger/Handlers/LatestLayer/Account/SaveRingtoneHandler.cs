using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Save or remove saved notification sound.
/// Possible errors
/// Code Type Description
/// 400 RINGTONE_INVALID The specified ringtone is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveRingtone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveRingtoneHandler(IMongoDatabase database, IFileReferenceHelper fileReferenceHelper) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveRingtone, MyTelegram.Schema.Account.ISavedRingtone>
{
    protected override async Task<MyTelegram.Schema.Account.ISavedRingtone> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSaveRingtone obj)
    {
        // Extract document ID
        long documentId = 0;
        if (obj.Id is MyTelegram.Schema.TInputDocument inputDoc)
        {
            documentId = inputDoc.Id;
        }
        else
        {
            RpcErrors.RpcErrors400.RingtoneInvalid.ThrowRpcError();
        }

        // Get document from database
        var docCollection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", documentId);
        var docDoc = await docCollection.Find(docFilter).FirstOrDefaultAsync();

        if (docDoc == null)
        {
            RpcErrors.RpcErrors400.RingtoneInvalid.ThrowRpcError();
        }

        var collection = database.GetCollection<BsonDocument>("saved_ringtones");

        if (obj.Unsave)
        {
            // Remove from saved list
            await collection.DeleteOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("DocumentId", documentId)
                )
            );

            // TSavedRingtone has no fields according to TL schema
            return new MyTelegram.Schema.Account.TSavedRingtone();
        }
        else
        {
            // Check if already saved
            var existsFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("DocumentId", documentId)
            );
            var exists = await collection.Find(existsFilter).AnyAsync();

            if (!exists)
            {
                // Add to saved list
                var doc = new BsonDocument
                {
                    ["UserId"] = input.UserId,
                    ["DocumentId"] = documentId,
                    ["SavedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await collection.InsertOneAsync(doc);
            }

            // Check if document is MP3
            var mimeType = docDoc.Contains("MimeType") ? docDoc["MimeType"].AsString : "";

            if (mimeType == "audio/mpeg" || mimeType == "audio/mp3")
            {
                // Already MP3, return empty (no conversion needed)
                return new MyTelegram.Schema.Account.TSavedRingtone();
            }
            else
            {
                // Would need conversion in real implementation
                // Return converted with document
                return new MyTelegram.Schema.Account.TSavedRingtoneConverted
                {
                    Document = ConvertToDocument(docDoc)
                };
            }
        }
    }

    private MyTelegram.Schema.IDocument ConvertToDocument(BsonDocument doc)
    {
        var attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>();

        // Add audio attributes if available
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
