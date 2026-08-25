namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// What counts as a GIF on Telegram. See https://corefork.telegram.org/api/gifs
///
/// <para>"On Telegram, GIFs are actually MPEG4 videos without sound" — a document only qualifies
/// when it carries <c>documentAttributeAnimated</c> <b>and</b> the <c>video/mp4</c> mime type.
/// Both halves matter: tdlib refuses to save anything else ("Only MPEG4 animations can be saved")
/// and tdesktop drops non-<c>isGifv()</c> documents out of the list it receives, which silently
/// makes its list shorter than ours and breaks the hash for good.</para>
/// </summary>
public static class GifDocumentHelper
{
    public const string Mp4MimeType = "video/mp4";

    /// <summary>True when the document is a GIF as clients define it: MPEG4 plus the animated flag.</summary>
    public static bool IsAnimatedMp4(IDocumentReadModel? document)
    {
        return document != null
            && string.Equals(document.MimeType, Mp4MimeType, StringComparison.OrdinalIgnoreCase)
            && HasAnimatedAttribute(document);
    }

    /// <inheritdoc cref="IsAnimatedMp4(IDocumentReadModel?)"/>
    public static bool IsAnimatedMp4(TDocument? document)
    {
        return document != null
            && string.Equals(document.MimeType, Mp4MimeType, StringComparison.OrdinalIgnoreCase)
            && document.Attributes?.Any(p => p is TDocumentAttributeAnimated) == true;
    }

    /// <summary>
    /// Whether the document is marked as an animation, ignoring its mime type. An
    /// <c>image/gif</c> upload lands here: animated, but not yet MPEG4, so it has to be converted
    /// before any client will treat it as a GIF.
    /// </summary>
    public static bool HasAnimatedAttribute(IDocumentReadModel? document)
    {
        return document?.Attributes2?.Any(p => p is TDocumentAttributeAnimated) == true;
    }

    /// <inheritdoc cref="HasAnimatedAttribute(IDocumentReadModel?)"/>
    public static bool HasAnimatedAttribute(TDocument? document)
    {
        return document?.Attributes?.Any(p => p is TDocumentAttributeAnimated) == true;
    }

    /// <summary>
    /// An animated document that is not yet MPEG4 — the case the server is expected to convert:
    /// "if the user tries to upload an actual GIF file, it will be automatically converted to an
    /// MPEG4 file by the server".
    /// </summary>
    public static bool NeedsMp4Conversion(TDocument? document)
    {
        return HasAnimatedAttribute(document) && !IsAnimatedMp4(document);
    }

    /// <summary>The document behind a <c>messageMediaDocument</c>, or null for any other media.</summary>
    public static TDocument? GetDocument(IMessageMedia? media)
    {
        return (media as TMessageMediaDocument)?.Document as TDocument;
    }
}
