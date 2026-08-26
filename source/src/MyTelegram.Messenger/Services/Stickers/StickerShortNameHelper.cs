namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// The stickerset short name rules, shared by <c>stickers.checkShortName</c>,
/// <c>stickers.suggestShortName</c> and <c>stickers.createStickerSet</c> so the three cannot disagree about
/// what is acceptable — a name the check accepted but the creation rejected is a dead end in the client's
/// pack-creation flow.
/// </summary>
public static class StickerShortNameHelper
{
    /// <summary>Telegram's own limit on <c>t.me/addstickers/&lt;name&gt;</c>.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Must start with a letter and hold only letters, digits and underscores. Compared
    /// case-insensitively elsewhere, because deep links are not case-normalised by clients.
    /// </summary>
    public static bool IsValid(string? shortName)
    {
        if (string.IsNullOrEmpty(shortName) || shortName.Length > MaxLength)
        {
            return false;
        }

        if (!char.IsAsciiLetter(shortName[0]))
        {
            return false;
        }

        return shortName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Derives a candidate from a pack title: spaces become underscores, anything else unusable is dropped.
    /// Returns "stickers" when nothing survives, which happens for a title written entirely in a script
    /// with no ASCII — a short name has to be ASCII to work as a link.
    /// </summary>
    public static string FromTitle(string? title)
    {
        var candidate = new string((title ?? string.Empty)
            .Replace(' ', '_')
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            .ToArray())
            .Trim('_')
            .ToLowerInvariant();

        // A leading digit is invalid, and trimming cannot fix that.
        candidate = candidate.TrimStart("0123456789".ToCharArray());

        if (candidate.Length > MaxLength - 4)
        {
            // Leave room for the numeric suffix a collision needs.
            candidate = candidate[..(MaxLength - 4)];
        }

        return candidate.Length == 0 ? "stickers" : candidate;
    }
}
