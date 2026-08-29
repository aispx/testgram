namespace MyTelegram.Messenger.Services;

/// <summary>
/// Refuses an outgoing media constructor whose <c>file_reference</c> this server did not issue, or issued
/// too long ago.
///
/// <para>Applied only to <c>messages.sendMedia</c> and <c>messages.sendMultiMedia</c>, the two methods
/// whose documented error lists carry <c>FILE_REFERENCE_*</c>. The other methods that accept an
/// <c>inputDocument</c> — <c>saveGif</c>, <c>faveSticker</c>, the <c>stickers.*</c> editors,
/// <c>editMessage</c>, <c>uploadMedia</c> — do not list those errors on the official server (measured
/// against corefork.telegram.org), and refusing there would break a client whose only fault is a cache
/// older than the reference lifetime.</para>
///
/// <para>See https://corefork.telegram.org/api/file-references</para>
/// </summary>
public static class InputMediaFileReferenceChecker
{
    /// <param name="index">
    /// Position of this media in the <c>multi_media</c> vector, for the indexed error form. Null for a
    /// single-media method.
    /// </param>
    public static void Check(IFileReferenceHelper fileReferenceHelper, IInputMedia? media, int? index = null)
    {
        switch (media)
        {
            case TInputMediaDocument { Id: TInputDocument document } inputMediaDocument:
                fileReferenceHelper.Check(document.FileReference.Span, AccessHashType.Document, document.Id, index);

                // A custom video cover is a second reference in the same constructor, and clients repair
                // it separately — hence the COVER_ spelling of the error.
                if (inputMediaDocument.VideoCover is TInputPhoto cover)
                {
                    fileReferenceHelper.Check(cover.FileReference.Span, AccessHashType.Photo, cover.Id, index,
                        isCover: true);
                }

                break;

            case TInputMediaPhoto { Id: TInputPhoto photo }:
                fileReferenceHelper.Check(photo.FileReference.Span, AccessHashType.Photo, photo.Id, index);
                break;

            // "The same FILE_REFERENCE_%d_INVALID error may also be emitted by messages.sendMedia [...]
            // when an inputMediaPaidMedia is provided with an array of extended_media": the index then
            // names a position in extended_media, so this only nests one level.
            case TInputMediaPaidMedia paidMedia when index == null:
                for (var i = 0; i < paidMedia.ExtendedMedia.Count; i++)
                {
                    Check(fileReferenceHelper, paidMedia.ExtendedMedia[i], i);
                }

                break;
        }
    }
}
