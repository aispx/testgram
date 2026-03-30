using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ToggleStarGiftsPinnedToTopHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestToggleStarGiftsPinnedToTop, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestToggleStarGiftsPinnedToTop obj)
    {
        var col = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        var pinnedIds = obj.Stargift
            .OfType<TInputSavedStarGiftChat>()
            .Select(g => g.SavedId)
            .ToHashSet();

        await col.UpdateManyAsync(
            d => d.OwnerUserId == input.UserId,
            Builders<SavedStarGiftDocument>.Update.Set(d => d.PinnedToTop, false));

        if (pinnedIds.Count > 0)
        {
            await col.UpdateManyAsync(
                d => d.OwnerUserId == input.UserId && pinnedIds.Contains(d.RandomId),
                Builders<SavedStarGiftDocument>.Update.Set(d => d.PinnedToTop, true));
        }

        return new TBoolTrue();
    }
}
