using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal static class StarGiftCollectionHelper
{
    public static (long userId, long channelId) GetOwner(IRequestInput input, IInputPeer peer) =>
        peer switch
        {
            TInputPeerChannel ch => (input.UserId, ch.ChannelId),
            TInputPeerSelf => (input.UserId, 0),
            TInputPeerUser u => (u.UserId, 0),
            _ => (input.UserId, 0)
        };

    public static FilterDefinition<StarGiftCollectionDocument> OwnerFilter(long userId, long channelId)
    {
        if (channelId > 0)
            return Builders<StarGiftCollectionDocument>.Filter.Or(
                Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.OwnerChannelId, channelId),
                Builders<StarGiftCollectionDocument>.Filter.And(
                    Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.OwnerUserId, userId),
                    Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.OwnerChannelId, 0L)
                )
            );
        return Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.OwnerUserId, userId);
    }

    public static StarGiftCollectionItem ToItem(IInputSavedStarGift gift) => gift switch
    {
        TInputSavedStarGiftUser u => new StarGiftCollectionItem { MsgId = u.MsgId },
        TInputSavedStarGiftChat c => new StarGiftCollectionItem { SavedId = c.SavedId },
        TInputSavedStarGiftSlug s => new StarGiftCollectionItem { Slug = s.Slug },
        _ => new StarGiftCollectionItem()
    };

    public static bool ItemEquals(StarGiftCollectionItem a, StarGiftCollectionItem b) =>
        (a.MsgId.HasValue && a.MsgId == b.MsgId) ||
        (a.SavedId.HasValue && a.SavedId == b.SavedId) ||
        (!string.IsNullOrEmpty(a.Slug) && a.Slug == b.Slug);

    public static IStarGiftCollection ToTl(StarGiftCollectionDocument doc) =>
        new TStarGiftCollection
        {
            CollectionId = doc.CollectionId,
            Title = doc.Title,
            GiftsCount = doc.Items.Count,
            Hash = doc.CollectionId ^ (long)doc.Items.Count << 32
        };
}
