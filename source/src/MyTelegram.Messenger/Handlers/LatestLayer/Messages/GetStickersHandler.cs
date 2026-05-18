using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get stickers by emoji
/// Possible errors
/// Code Type Description
/// 400 EMOTICON_EMPTY The emoji is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStickersHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetStickers, MyTelegram.Schema.Messages.IStickers>
{
    protected override async Task<IStickers> HandleCoreAsync(IRequestInput input, RequestGetStickers obj)
    {
        if (string.IsNullOrEmpty(obj.Emoticon))
            RpcErrors.RpcErrors400.EmoticonEmpty.ThrowRpcError();

        // Item 17: actually surface stickers for the requested emoji so the client can
        // render a randomised greeting/wave (👋) animation for first-time chats instead
        // of falling back to a static placeholder. We scan every sticker set for packs
        // whose emoticon matches, gather the referenced documents and return them in a
        // shuffled order; the client picks the first one for the welcome animation.
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var sets = await setCol.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();

        var matchingDocIds = new List<long>();
        foreach (var setDoc in sets)
        {
            var isEmojiSet = setDoc.Contains("Emojis") && setDoc["Emojis"].ToBoolean();
            if (isEmojiSet)
            {
                continue;
            }

            if (!setDoc.Contains("Packs") || setDoc["Packs"].IsBsonNull) continue;
            foreach (var p in setDoc["Packs"].AsBsonArray)
            {
                if (p["Emoticon"].AsString != obj.Emoticon) continue;
                foreach (var d in p["Documents"].AsBsonArray)
                {
                    matchingDocIds.Add(d.IsInt64 ? d.AsInt64 : d.AsInt32);
                }
            }
        }

        if (matchingDocIds.Count == 0)
        {
            return new TStickers { Hash = obj.Hash, Stickers = new TVector<IDocument>() };
        }

        // Shuffle so callers asking for the same emoticon don't always get the same
        // sticker first — gives the "randomised greeting" behaviour the user expects.
        for (int i = matchingDocIds.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (matchingDocIds[i], matchingDocIds[j]) = (matchingDocIds[j], matchingDocIds[i]);
        }

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docDocs = await docCol.Find(Builders<BsonDocument>.Filter.In("DocumentId", matchingDocIds)).ToListAsync();
        var byId = docDocs.ToDictionary(d => d["DocumentId"].IsInt64 ? d["DocumentId"].AsInt64 : (long)d["DocumentId"].AsInt32);

        var stickers = new TVector<IDocument>();
        foreach (var id in matchingDocIds)
        {
            if (!byId.TryGetValue(id, out var d)) continue;
            byte[] fileRef = [];
            if (d.Contains("FileReference") && !d["FileReference"].IsBsonNull)
            {
                var fr = d["FileReference"];
                if (fr.BsonType == BsonType.Binary) fileRef = fr.AsBsonBinaryData.Bytes;
                else if (fr.BsonType == BsonType.Array) fileRef = fr.AsBsonArray.Select(b => (byte)b.AsInt32).ToArray();
            }
            stickers.Add(new TDocument
            {
                Id = d["DocumentId"].IsInt64 ? d["DocumentId"].AsInt64 : d["DocumentId"].AsInt32,
                AccessHash = d["AccessHash"].IsInt64 ? d["AccessHash"].AsInt64 : d["AccessHash"].AsInt32,
                FileReference = fileRef,
                Date = d["Date"].AsInt32,
                MimeType = d.Contains("MimeType") && !d["MimeType"].IsBsonNull ? d["MimeType"].AsString : "image/webp",
                Size = d["Size"].IsInt64 ? d["Size"].AsInt64 : d["Size"].AsInt32,
                DcId = d["DcId"].AsInt32,
                Attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeSticker
                {
                    Alt = obj.Emoticon,
                    Stickerset = new TInputStickerSetEmpty(),
                    Mask = false,
                }),
                Thumbs = new TVector<IPhotoSize>(),
                VideoThumbs = new TVector<IVideoSize>(),
            });
        }

        return new TStickers
        {
            Hash = obj.Hash,
            Stickers = stickers,
        };
    }
}
