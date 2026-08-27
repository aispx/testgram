using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Apply changes to multiple stickersets
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleStickerSets"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleStickerSetsHandler(
    IStickerSetStore stickerSetStore,
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleStickerSets, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestToggleStickerSets obj)
    {
        if (obj.Stickersets == null || obj.Stickersets.Count == 0)
        {
            return new TBoolTrue();
        }

        // The flags are mutually exclusive in every client, but the schema does not say so; act on the
        // most destructive one first so an ambiguous request can never leave a set half-toggled.
        var uninstall = obj.Uninstall;
        var archive = !uninstall && obj.Archive;
        var unarchive = !uninstall && !archive && obj.Unarchive;

        if (!uninstall && !archive && !unarchive)
        {
            return new TBoolTrue();
        }

        // Resolve first, so one unknown set does not silently skip the rest of the batch.
        var byType = new Dictionary<StickerSetType, List<long>>();
        foreach (var inputStickerSet in obj.Stickersets)
        {
            var lookup = await stickerSetStore.FindAsync(inputStickerSet);
            if (lookup.Set == null)
            {
                continue;
            }

            var type = stickerSetStore.GetStickerSetType(lookup.Set);
            if (!byType.TryGetValue(type, out var ids))
            {
                ids = [];
                byType[type] = ids;
            }

            ids.Add(lookup.Set.GetInt64("StickerSetId"));
        }

        foreach (var (type, stickerSetIds) in byType)
        {
            var changed = uninstall
                ? await UninstallAsync(input.UserId, stickerSetIds)
                : (await installedStickerSetStore.SetArchivedAsync(input.UserId, stickerSetIds, archive)).Count > 0;

            if (changed)
            {
                await updateNotifier.NotifyStickerSetsAsync(input.UserId, type, input.AuthKeyId);
            }
        }

        return new TBoolTrue();
    }

    private async Task<bool> UninstallAsync(long userId, List<long> stickerSetIds)
    {
        var changed = false;
        foreach (var stickerSetId in stickerSetIds)
        {
            changed |= await installedStickerSetStore.UninstallAsync(userId, stickerSetId);
        }

        return changed;
    }
}
