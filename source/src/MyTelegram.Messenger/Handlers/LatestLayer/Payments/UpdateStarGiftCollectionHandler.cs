using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class UpdateStarGiftCollectionHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestUpdateStarGiftCollection, MyTelegram.Schema.IStarGiftCollection>
{
    protected override async Task<IStarGiftCollection> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestUpdateStarGiftCollection obj)
    {
        var col = mongoDatabase.GetCollection<StarGiftCollectionDocument>("star-gift-collections");
        var (userId, channelId) = StarGiftCollectionHelper.GetOwner(input, obj.Peer);

        var filter = StarGiftCollectionHelper.OwnerFilter(userId, channelId)
            & Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.CollectionId, obj.CollectionId);

        var doc = await col.Find(filter).FirstOrDefaultAsync()
            ?? throw new Exception("COLLECTION_INVALID");

        // Apply title
        if (obj.Title != null) doc.Title = obj.Title;

        // Remove gifts
        if (obj.DeleteStargift?.Count > 0)
        {
            var toRemove = obj.DeleteStargift.Select(StarGiftCollectionHelper.ToItem).ToList();
            doc.Items.RemoveAll(item => toRemove.Any(r => StarGiftCollectionHelper.ItemEquals(item, r)));
        }

        // Add gifts
        if (obj.AddStargift?.Count > 0)
        {
            foreach (var gift in obj.AddStargift)
            {
                var item = StarGiftCollectionHelper.ToItem(gift);
                if (!doc.Items.Any(x => StarGiftCollectionHelper.ItemEquals(x, item)))
                    doc.Items.Add(item);
            }
        }

        // Reorder
        if (obj.Order?.Count > 0)
        {
            var ordered = obj.Order.Select(StarGiftCollectionHelper.ToItem).ToList();
            doc.Items = ordered.Where(o => doc.Items.Any(x => StarGiftCollectionHelper.ItemEquals(x, o))).ToList();
        }

        await col.ReplaceOneAsync(filter, doc);
        return StarGiftCollectionHelper.ToTl(doc);
    }
}
