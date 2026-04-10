using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetResaleStarGiftsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetResaleStarGifts, IResaleStarGifts>
{
    protected override async Task<IResaleStarGifts> HandleCoreAsync(IRequestInput input, RequestGetResaleStarGifts obj)
    {
        var col = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

        var filter = Builders<UniqueStarGiftDocument>.Filter.And(
            Builders<UniqueStarGiftDocument>.Filter.Eq(d => d.GiftId, obj.GiftId),
            Builders<UniqueStarGiftDocument>.Filter.Gt(d => d.ResellStars, 0L)
        );

        SortDefinition<UniqueStarGiftDocument> sort = obj.SortByPrice
            ? Builders<UniqueStarGiftDocument>.Sort.Ascending(d => d.ResellStars)
            : obj.SortByNum
                ? Builders<UniqueStarGiftDocument>.Sort.Ascending(d => d.Num)
                : Builders<UniqueStarGiftDocument>.Sort.Descending(d => d.Date);

        int skip = int.TryParse(obj.Offset, out var s) ? s : 0;
        var docs = await col.Find(filter).Sort(sort).Skip(skip).Limit(obj.Limit).ToListAsync();

        var gifts = new TVector<IStarGift>(docs.Select(d => (IStarGift)UniqueStarGiftHelper.ToTl(d)).ToList());

        int nextSkip = skip + docs.Count;
        var total = (int)await col.CountDocumentsAsync(filter);
        string? nextOffset = nextSkip < total ? nextSkip.ToString() : null;

        return new TResaleStarGifts
        {
            Count = total,
            Gifts = gifts,
            NextOffset = nextOffset,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
        };
    }
}
