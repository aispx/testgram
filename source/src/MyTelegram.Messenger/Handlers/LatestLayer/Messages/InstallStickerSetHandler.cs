using MyTelegram.Messenger.Services.Stickers;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Install a stickerset.
/// Possible errors
/// Code Type Description
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.installStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InstallStickerSetHandler(
    IStickerSetStore stickerSetStore,
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerSetMapper stickerSetMapper,
    IStickerLimitResolver limitResolver,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<RequestInstallStickerSet, IStickerSetInstallResult>
{
    protected override async Task<IStickerSetInstallResult> HandleCoreAsync(IRequestInput input,
        RequestInstallStickerSet obj)
    {
        var lookup = await stickerSetStore.FindAsync(obj.Stickerset);
        if (lookup.Set == null)
        {
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var setDocument = lookup.Set!;
        var setId = setDocument.GetInt64("StickerSetId");
        var type = stickerSetStore.GetStickerSetType(setDocument);

        // Installing a set the user already has, with archived unset, is how clients un-archive it — the
        // store rewrites Archived either way and moves the set back to the front.
        await installedStickerSetStore.InstallAsync(input.UserId, setId, type, obj.Archived);

        var stickerSet = await stickerSetMapper.BuildFullAsync(input, setDocument, lookup.Emoticon);

        await updateNotifier.NotifyNewStickerSetAsync(input.UserId, stickerSet, input.AuthKeyId);
        await updateNotifier.NotifyStickerSetsAsync(input.UserId, type, input.AuthKeyId);

        if (obj.Archived)
        {
            return new TStickerSetInstallResultSuccess();
        }

        // Past the limit the server archives the least recently used sets and says so, rather than
        // refusing the install. Clients show the returned covers in an "archived stickers" popup.
        var archived = await installedStickerSetStore.ArchiveOverflowAsync(input.UserId, type,
            limitResolver.GetInstalledLimit());
        if (archived.Count == 0)
        {
            return new TStickerSetInstallResultSuccess();
        }

        var archivedCatalogue = await stickerSetStore.FindManyAsync(archived);
        var covered = await stickerSetMapper.BuildCoveredAsync(input,
            archived.Where(archivedCatalogue.ContainsKey).Select(p => archivedCatalogue[p]).ToList(), false);

        return new TStickerSetInstallResultArchive { Sets = new TVector<IStickerSetCovered>(covered) };
    }
}
