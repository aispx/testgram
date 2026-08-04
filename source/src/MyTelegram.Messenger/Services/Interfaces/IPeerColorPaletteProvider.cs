namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Provides the server's <a href="https://core.telegram.org/api/colors">peer color palettes</a>
/// for both message accents (help.getPeerColors) and profile pages (help.getPeerProfileColors),
/// so that the palette definitions, their hash and the validation of incoming color ids
/// can never drift apart.
/// </summary>
public interface IPeerColorPaletteProvider
{
    /// <summary>Palettes usable for message accents (help.getPeerColors).</summary>
    IReadOnlyList<IPeerColorOption> GetMessageColorOptions();

    /// <summary>Palettes usable for profile page backgrounds (help.getPeerProfileColors).</summary>
    IReadOnlyList<IPeerColorOption> GetProfileColorOptions();

    /// <summary>
    /// Hash of the supplied palette list, for the <c>hash</c> field of help.PeerColors.
    /// See https://core.telegram.org/api/offsets#hash-generation
    /// </summary>
    int ComputeHash(IEnumerable<IPeerColorOption> options);

    /// <summary>
    /// Returns the palette option with the given id, or <c>null</c> if the id is not served
    /// (callers should then throw COLOR_INVALID).
    /// </summary>
    IPeerColorOption? GetOption(int colorId, bool forProfile);
}
