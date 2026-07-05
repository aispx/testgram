using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Get Telegram Premium promotion information
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getPremiumPromo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPremiumPromoHandler(
    IMongoDatabase mongoDatabase,
    ILayeredService<IPremiumPromoConverter> layeredPremiumPromoService) : RpcResultObjectHandler<MyTelegram.Schema.Help.RequestGetPremiumPromo, MyTelegram.Schema.Help.IPremiumPromo>
{
    protected override async Task<MyTelegram.Schema.Help.IPremiumPromo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Help.RequestGetPremiumPromo obj)
    {
        var promo = (TPremiumPromo)layeredPremiumPromoService.GetConverter(input.Layer).ToPremiumPromo();
        var videos = await LoadPromoVideosAsync();
        promo.VideoSections = new TVector<string>(videos.Select(x => x.Section).ToList());
        promo.Videos = new TVector<IDocument>(videos.Select(x => x.Document).ToList());
        return promo;
    }

    private async Task<List<(string Section, IDocument Document)>> LoadPromoVideosAsync()
    {
        var promoCol = mongoDatabase.GetCollection<BsonDocument>("premium_promo_media");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var promoDocs = await promoCol.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("Order").Ascending("Section"))
            .ToListAsync();
        if (promoDocs.Count == 0)
        {
            return [];
        }

        var documentIds = promoDocs
            .Where(x => x.Contains("DocumentId") && !x["DocumentId"].IsBsonNull)
            .Select(x => GetInt64(x["DocumentId"]))
            .Distinct()
            .ToList();
        if (documentIds.Count == 0)
        {
            return [];
        }

        var docDocs = await docCol.Find(Builders<BsonDocument>.Filter.In("DocumentId", documentIds.Select(x => (BsonValue)new BsonInt64(x))))
            .ToListAsync();
        var docMap = docDocs.ToDictionary(x => GetInt64(x["DocumentId"]));
        var result = new List<(string Section, IDocument Document)>();

        foreach (var promoDoc in promoDocs)
        {
            if (!promoDoc.Contains("Section") || !promoDoc.Contains("DocumentId"))
            {
                continue;
            }

            var documentId = GetInt64(promoDoc["DocumentId"]);
            if (!docMap.TryGetValue(documentId, out var doc))
            {
                continue;
            }

            result.Add((promoDoc["Section"].AsString, BuildDocument(doc)));
        }

        return result;
    }

    private static IDocument BuildDocument(BsonDocument d)
    {
        byte[] fileRef = [];
        if (d.Contains("FileReference") && !d["FileReference"].IsBsonNull)
        {
            var fr = d["FileReference"];
            if (fr.BsonType == BsonType.Binary)
                fileRef = fr.AsBsonBinaryData.Bytes;
            else if (fr.BsonType == BsonType.Array)
                fileRef = fr.AsBsonArray.Select(x => (byte)GetInt32(x)).ToArray();
        }

        TVector<IDocumentAttribute> attributes = [];
        if (d.Contains("Attributes2") && !d["Attributes2"].IsBsonNull)
        {
            try
            {
                attributes = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(d["Attributes2"].ToJson());
            }
            catch
            {
                attributes = [];
            }
        }

        return new TDocument
        {
            Id = GetInt64(d["DocumentId"]),
            AccessHash = GetInt64(d["AccessHash"]),
            FileReference = fileRef,
            Date = d.Contains("Date") ? GetInt32(d["Date"]) : 0,
            MimeType = d.Contains("MimeType") ? d["MimeType"].AsString : "application/octet-stream",
            Size = d.Contains("Size") ? GetInt64(d["Size"]) : 0,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            // dc_id=0 points the client at a non-existent datacenter and makes it spam
            // help.getConfig; fall back to the media DC like the other document builders.
            DcId = d.Contains("DcId") && GetInt32(d["DcId"]) > 0 ? GetInt32(d["DcId"]) : MyTelegramConsts.MediaDcId,
            Attributes = attributes
        };
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }

    private static int GetInt32(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => (int)v.AsInt64,
            BsonType.Double => (int)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int32")
        };
    }
}