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

        // Layer 206: a gift is considered "on resale" if it has either a Stars
        // or a TON price. The sort-by-price order still uses Stars as the
        // primary key for backward compat; gifts priced only in TON appear
        // after Stars-priced ones.
        var filter = Builders<UniqueStarGiftDocument>.Filter.And(
            Builders<UniqueStarGiftDocument>.Filter.Eq(d => d.GiftId, obj.GiftId),
            Builders<UniqueStarGiftDocument>.Filter.Or(
                Builders<UniqueStarGiftDocument>.Filter.Gt(d => d.ResellStars, 0L),
                Builders<UniqueStarGiftDocument>.Filter.Gt(d => d.ResellTon, 0L)
            )
        );

        SortDefinition<UniqueStarGiftDocument> sort = obj.SortByPrice
            ? Builders<UniqueStarGiftDocument>.Sort.Ascending(d => d.ResellStars).Ascending(d => d.ResellTon)
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
