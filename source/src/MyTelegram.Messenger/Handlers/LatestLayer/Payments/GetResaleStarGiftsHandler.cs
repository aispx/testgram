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

        // Item 6: honour the StarsOnly flag (hide TON-only listings) and the ForCraft
        // flag (only crafted-eligible gifts) so the marketplace filter chips actually
        // narrow the result list instead of being silently ignored.
        if (obj.StarsOnly)
        {
            filter &= Builders<UniqueStarGiftDocument>.Filter.Gt(d => d.ResellStars, 0L);
            filter &= Builders<UniqueStarGiftDocument>.Filter.Ne(d => d.ResaleTonOnly, true);
        }
        if (obj.ForCraft)
        {
            // A gift can be used as a craft input only when it hasn't been burned.
            filter &= Builders<UniqueStarGiftDocument>.Filter.Ne(d => d.Burned, true);
        }

        SortDefinition<UniqueStarGiftDocument> sort = obj.SortByPrice
            ? Builders<UniqueStarGiftDocument>.Sort.Ascending(d => d.ResellStars).Ascending(d => d.ResellTon)
            : obj.SortByNum
                ? Builders<UniqueStarGiftDocument>.Sort.Ascending(d => d.Num)
                : Builders<UniqueStarGiftDocument>.Sort.Descending(d => d.Date);

        int skip = int.TryParse(obj.Offset, out var s) ? s : 0;

        // Item 6: load full set then apply in-memory attribute filter. Attributes are
        // nested arrays of dynamic shape so a Mongo-side query would need an
        // ElemMatch per attribute id; the simpler client-side filter is acceptable
        // because the per-gift result-set is bounded (a few thousand at most).
        var allMatching = await col.Find(filter).Sort(sort).ToListAsync();
        if (obj.Attributes != null && obj.Attributes.Count > 0)
        {
            var modelDocIds = obj.Attributes.OfType<TStarGiftAttributeIdModel>().Select(a => a.DocumentId).ToHashSet();
            var patternDocIds = obj.Attributes.OfType<TStarGiftAttributeIdPattern>().Select(a => a.DocumentId).ToHashSet();
            var backdropIds = obj.Attributes.OfType<TStarGiftAttributeIdBackdrop>().Select(a => a.BackdropId).ToHashSet();

            bool MatchesAny(UniqueStarGiftDocument d)
            {
                bool ok = true;
                if (modelDocIds.Count > 0)
                    ok &= d.Attributes.Any(a => a.Type == "model" && a.DocumentId.HasValue && modelDocIds.Contains(a.DocumentId.Value));
                if (ok && patternDocIds.Count > 0)
                    ok &= d.Attributes.Any(a => a.Type == "pattern" && a.DocumentId.HasValue && patternDocIds.Contains(a.DocumentId.Value));
                if (ok && backdropIds.Count > 0)
                    ok &= d.Attributes.Any(a => a.Type == "backdrop" && a.BackdropId.HasValue && backdropIds.Contains(a.BackdropId.Value));
                return ok;
            }

            allMatching = allMatching.Where(MatchesAny).ToList();
        }

        var docs = allMatching.Skip(skip).Take(obj.Limit > 0 ? obj.Limit : 30).ToList();

        var gifts = new TVector<IStarGift>(docs.Select(d => (IStarGift)UniqueStarGiftHelper.ToTl(d)).ToList());

        int nextSkip = skip + docs.Count;
        var total = allMatching.Count;
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
