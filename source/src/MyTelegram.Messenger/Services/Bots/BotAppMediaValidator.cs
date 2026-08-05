using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Validates media uploaded to BotFather during the web app flows.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="BotFatherBotService"/> so the rules can be tested without standing
/// up Mongo and Redis. See https://corefork.telegram.org/api/bots/webapps .
/// </remarks>
public static class BotAppMediaValidator
{
    /// <summary>Web app preview photos must be exactly this size, as upstream BotFather requires.</summary>
    public const int RequiredPhotoWidth = 640;

    public const int RequiredPhotoHeight = 360;

    /// <summary>
    /// True when the media is a photo that was uploaded at exactly 640x360.
    /// </summary>
    /// <remarks>
    /// Clients upload several scaled sizes for one photo, so the requirement is met if any of them
    /// is the required size — a photo that was never 640x360 cannot produce such a size.
    /// </remarks>
    public static bool IsValidPreviewPhoto(IMessageMedia? media)
    {
        if (media is not TMessageMediaPhoto { Photo: TPhoto photo })
        {
            return false;
        }

        foreach (var size in photo.Sizes)
        {
            var (width, height) = size switch
            {
                TPhotoSize s => (s.W, s.H),
                TPhotoSizeProgressive s => (s.W, s.H),
                _ => (0, 0)
            };

            if (width == RequiredPhotoWidth && height == RequiredPhotoHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the media is an animation. A GIF reaches the server as a document carrying
    /// <c>documentAttributeAnimated</c>; a plain video or photo does not qualify.
    /// </summary>
    public static bool IsAnimation(IMessageMedia? media)
    {
        if (media is not TMessageMediaDocument { Document: TDocument document })
        {
            return false;
        }

        return document.Attributes.Any(a => a is TDocumentAttributeAnimated);
    }
}
