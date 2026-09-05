namespace MyTelegram.Messenger.Services.Translation;

/// <summary>
/// Maps the <c>to_lang</c> a client sends onto a DeepL target language.
///
/// <para>The two vocabularies do not line up. Telegram documents <c>to_lang</c> as "two-letter ISO
/// 639-1", but Android normalises whatever the platform reports through
/// <c>TranslateController.normalizeLanguage</c> — lowercase primary tag, uppercase region — so
/// <c>pt-BR</c>, <c>zh-HANS</c> and <c>en-US</c> all arrive here. DeepL in turn wants its own casing and
/// insists on a region for a few languages (<c>EN-US</c>/<c>EN-GB</c>, <c>PT-BR</c>/<c>PT-PT</c>,
/// <c>ZH-HANS</c>/<c>ZH-HANT</c>) while refusing one for the rest.</para>
///
/// <para>The target set is pinned rather than fetched from <c>/v2/languages?type=target</c> on demand:
/// <c>messages.translateText</c> is on the hot path of a chat being scrolled, and a language list that
/// changes a few times a year is not worth a round trip per call. It was read off the live API and is
/// the 111 targets DeepL served.</para>
/// </summary>
internal static class TranslationLanguageMap
{
    /// <summary>Every target DeepL accepts, as read from <c>/v2/languages?type=target</c>.</summary>
    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        "AF", "AN", "AR", "AS", "AY", "AZ", "BA", "BE", "BG", "BN", "BR", "BS", "CA", "CS", "CY", "DA",
        "DE", "DE-CH", "DE-DE", "EL", "EN-GB", "EN-US", "EO", "ES", "ES-419", "ET", "EU", "FA", "FI",
        "FR", "FR-CA", "FR-FR", "GA", "GL", "GN", "GU", "HA", "HE", "HI", "HR", "HT", "HU", "HY", "ID",
        "IG", "IS", "IT", "JA", "JV", "KA", "KK", "KO", "KY", "LA", "LB", "LN", "LT", "LV", "MG", "MI",
        "MK", "ML", "MN", "MR", "MS", "MT", "MY", "NB", "NE", "NL", "OC", "OM", "PA", "PL", "PS",
        "PT-BR", "PT-PT", "QU", "RO", "RU", "SA", "SK", "SL", "SQ", "SR", "ST", "SU", "SV", "SW", "TA",
        "TE", "TG", "TH", "TK", "TL", "TN", "TR", "TS", "TT", "UK", "UR", "UZ", "VI", "WO", "XH", "YI",
        "ZH", "ZH-HANS", "ZH-HANT", "ZU"
    };

    /// <summary>
    /// The targets DeepL applies <c>formality</c> to. Sending it for any other target is a 400 in the
    /// strict form, so <c>tone</c> is simply dropped for the rest — no error is documented for a tone
    /// the server cannot honour, and refusing the whole translation over it would be worse.
    /// </summary>
    private static readonly HashSet<string> FormalityTargets = new(StringComparer.Ordinal)
    {
        "DE", "DE-CH", "DE-DE", "ES", "ES-419", "FR", "FR-CA", "FR-FR", "IT", "JA", "NL", "PL",
        "PT-BR", "PT-PT", "RU"
    };

    /// <summary>
    /// Codes that need more than uppercasing. The bare forms of the region-only languages are here
    /// because DeepL rejects <c>EN</c> and <c>PT</c> outright, and the legacy ISO codes are here because
    /// older Android builds still report them.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["EN"] = "EN-US",
        ["PT"] = "PT-PT",
        ["ZH-CN"] = "ZH-HANS",
        ["ZH-SG"] = "ZH-HANS",
        ["ZH-HANS"] = "ZH-HANS",
        ["ZH-TW"] = "ZH-HANT",
        ["ZH-HK"] = "ZH-HANT",
        ["ZH-MO"] = "ZH-HANT",
        ["ZH-HANT"] = "ZH-HANT",
        ["NO"] = "NB",
        ["NN"] = "NB",
        ["NO-NO"] = "NB",
        // Java's Locale still emits the pre-1989 codes for these three.
        ["IW"] = "HE",
        ["IN"] = "ID",
        ["JI"] = "YI",
        ["FIL"] = "TL",
        ["ES-MX"] = "ES-419",
        ["ES-AR"] = "ES-419",
        ["ES-CO"] = "ES-419",
        ["ES-CL"] = "ES-419",
        ["ES-419"] = "ES-419"
    };

    /// <summary>
    /// The DeepL target for a client's <c>to_lang</c>, or null when there is none — which the caller
    /// turns into <c>TO_LANG_INVALID</c>.
    /// </summary>
    public static string? Resolve(string? toLang)
    {
        if (string.IsNullOrWhiteSpace(toLang))
        {
            return null;
        }

        var normalized = toLang.Trim().Replace('_', '-').ToUpperInvariant();

        if (Aliases.TryGetValue(normalized, out var alias))
        {
            return alias;
        }

        if (Targets.Contains(normalized))
        {
            return normalized;
        }

        // A region DeepL does not know about ("de-AT", "ru-RU") is still that language.
        var dash = normalized.IndexOf('-');

        if (dash > 0)
        {
            var primary = normalized[..dash];

            if (Aliases.TryGetValue(primary, out var primaryAlias))
            {
                return primaryAlias;
            }

            if (Targets.Contains(primary))
            {
                return primary;
            }
        }

        return null;
    }

    /// <summary>Whether <c>formality</c> may be sent for this DeepL target.</summary>
    public static bool SupportsFormality(string deepLTarget)
    {
        return FormalityTargets.Contains(deepLTarget);
    }

    /// <summary>
    /// The DeepL <c>formality</c> value for a Telegram <c>tone</c>. tdlib only ever sends
    /// <c>formal</c> or <c>casual</c> (<c>TranslationManager::check_tone</c> clears <c>neutral</c> and
    /// rejects anything else), and an unknown tone is ignored rather than refused — the method
    /// documents no error for it, and a client that adds a tone before this server does should still
    /// get its translation.
    /// </summary>
    public static string? ResolveFormality(string? tone)
    {
        return tone?.Trim().ToLowerInvariant() switch
        {
            "formal" => "prefer_more",
            "casual" => "prefer_less",
            _ => null
        };
    }
}
