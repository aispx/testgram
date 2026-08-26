namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// The server-side half of the <c>update_stickersets_order</c> flag on the send methods: a sticker the user
/// just sent moves its set to the front of the panel.
///
/// <para>The flag is what the client sets when the sticker came from the panel rather than from a search or
/// a forward, and it expects the reorder to be remembered — the panel is re-read from
/// <c>messages.getAllStickers</c>, so a client that reordered only locally loses it on the next refresh.
/// The matching <c>updateMoveStickerSetToTop</c> tells the user's other sessions.</para>
/// See https://corefork.telegram.org/api/stickers#recent-stickersets
/// </summary>
public interface ISentStickerProcessor
{
    Task ProcessAsync(IRequestInput input, IMessageMedia? media, bool updateStickersetsOrder,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class SentStickerProcessor(
    IStickerSetStore stickerSetStore,
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerUpdateNotifier updateNotifier,
    ILogger<SentStickerProcessor> logger) : ISentStickerProcessor, ITransientDependency
{
    public async Task ProcessAsync(IRequestInput input, IMessageMedia? media, bool updateStickersetsOrder,
        CancellationToken cancellationToken = default)
    {
        if (!updateStickersetsOrder)
        {
            return;
        }

        if (media is not TMessageMediaDocument { Document: TDocument document })
        {
            return;
        }

        // Only a sticker moves a set; a photo or a plain file has none.
        var stickerset = document.Attributes?
            .Select(p => p switch
            {
                TDocumentAttributeSticker sticker => sticker.Stickerset,
                TDocumentAttributeCustomEmoji customEmoji => customEmoji.Stickerset,
                _ => null
            })
            .FirstOrDefault(p => p is TInputStickerSetID);

        if (stickerset == null)
        {
            return;
        }

        try
        {
            // The set is resolved from the document rather than from the attribute's id, because the id a
            // client echoes back arrives with a per-session access hash and may name a set it no longer has.
            var setDocument = await stickerSetStore.FindByDocumentIdAsync(document.Id, cancellationToken);
            if (setDocument == null)
            {
                return;
            }

            var setId = setDocument.GetInt64("StickerSetId");
            if (!await installedStickerSetStore.MoveToTopAsync(input.UserId, setId, cancellationToken))
            {
                // Not installed, or archived: there is no panel position to change.
                return;
            }

            await updateNotifier.NotifyMoveToTopAsync(input.UserId,
                stickerSetStore.GetStickerSetType(setDocument), setId, input.AuthKeyId);
        }
        catch (Exception exception)
        {
            // Reordering the panel must never cost the user their message.
            logger.LogWarning(exception, "Failed to move sticker set of document {DocumentId} to top",
                document.Id);
        }
    }
}
