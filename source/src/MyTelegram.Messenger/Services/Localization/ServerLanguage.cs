namespace MyTelegram.Messenger.Services.Localization;

/// <summary>
/// The languages server-generated texts (PSA messages, service messages) are available in.
/// Anything the server cannot map falls back to <see cref="Default"/>.
/// </summary>
public static class ServerLanguage
{
    public const string English = "en";
    public const string Russian = "ru";
    public const string Ukrainian = "uk";

    /// <summary>Used when the client reported no language, or one that has no translation.</summary>
    public const string Default = English;

    /// <summary>
    /// Normalizes a client-supplied <c>lang_code</c> ("ru-RU", "RU", "en_US", …) to a supported
    /// language. Unknown languages resolve to <see cref="Default"/>.
    /// </summary>
    public static string Normalize(string? langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode))
        {
            return Default;
        }

        var separator = langCode.AsSpan().IndexOfAny('-', '_');
        var primary = separator < 0 ? langCode : langCode[..separator];

        return primary.Trim().ToLowerInvariant() switch
        {
            Russian => Russian,
            English => English,
            Ukrainian => Ukrainian,
            _ => Default
        };
    }
}
