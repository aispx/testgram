using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Mark new featured stickers as read
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readFeaturedStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadFeaturedStickersHandler(
    IFeaturedStickerSetStore featuredStickerSetStore,
    IStickerSetStore stickerSetStore,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadFeaturedStickers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestReadFeaturedStickers obj)
    {
        // An empty vector means "clear the whole badge": Android sends the method with no ids from
        // markFeaturedStickersAsRead and only fills a single id when one set was opened
        // (MediaDataController lines 2480 and 2504). The request carries no masks/emojis flag either, so
        // the bare form has to clear both taxonomies.
        if (obj.Id == null || obj.Id.Count == 0)
        {
            foreach (var type in (StickerSetType[])[StickerSetType.Regular, StickerSetType.CustomEmoji])
            {
                var featured = await featuredStickerSetStore.GetFeaturedAsync(type);
                var ids = featured.ConvertAll(p => p.GetInt64("StickerSetId"));

                if (await featuredStickerSetStore.MarkReadAsync(input.UserId, type, ids))
                {
                    await updateNotifier.NotifyReadFeaturedAsync(input.UserId, type, input.AuthKeyId);
                }
            }

            return new TBoolTrue();
        }

        var catalogue = await stickerSetStore.FindManyAsync([..obj.Id]);
        var byType = obj.Id
            .Where(catalogue.ContainsKey)
            .GroupBy(p => stickerSetStore.GetStickerSetType(catalogue[p]));

        foreach (var group in byType)
        {
            if (await featuredStickerSetStore.MarkReadAsync(input.UserId, group.Key, group.ToList()))
            {
                await updateNotifier.NotifyReadFeaturedAsync(input.UserId, group.Key, input.AuthKeyId);
            }
        }

        return new TBoolTrue();
    }
}
