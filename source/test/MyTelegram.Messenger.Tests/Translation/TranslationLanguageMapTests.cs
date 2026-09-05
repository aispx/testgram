using MyTelegram.Messenger.Services.Translation;

namespace MyTelegram.Messenger.Tests.Translation;

/// <summary>
/// Feature: turning the <c>to_lang</c> a client sends into a provider language code.
///
/// <para>The codes do not line up on either side. Telegram documents <c>to_lang</c> as ISO 639-1, but
/// Android normalises whatever the platform reports (<c>TranslateController.normalizeLanguage</c>:
/// lowercase primary, uppercase region), so <c>pt-BR</c>, <c>zh-HANS</c> and <c>en-US</c> all arrive.
/// DeepL in turn refuses bare <c>EN</c> and <c>PT</c> and insists on a region for them, which is the one
/// mapping that cannot be derived by uppercasing.</para>
///
/// <para>Getting this wrong is <c>TO_LANG_INVALID</c> for a language the provider supports, which reads
/// to a user as "this server cannot translate into my language".</para>
/// </summary>
public class TranslationLanguageMapTests
{
    [Theory]
    [InlineData("ru", "RU")]
    [InlineData("RU", "RU")]
    [InlineData("de", "DE")]
    [InlineData("uk", "UK")]
    public void A_plain_two_letter_code_is_uppercased(string toLang, string expected)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBe(expected);
    }

    /// <summary>
    /// DeepL answers <c>Value for 'target_lang' not supported</c> for bare <c>EN</c> and <c>PT</c>
    /// (measured), so a client asking for "en" has to be given a region or it gets no translation at all.
    /// </summary>
    [Theory]
    [InlineData("en", "EN-US")]
    [InlineData("pt", "PT-PT")]
    public void A_language_the_provider_only_knows_by_region_gets_one(string toLang, string expected)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBe(expected);
    }

    [Theory]
    [InlineData("pt-BR", "PT-BR")]
    [InlineData("pt-br", "PT-BR")]
    [InlineData("PT_BR", "PT-BR")]
    [InlineData("en-GB", "EN-GB")]
    [InlineData("zh-HANS", "ZH-HANS")]
    [InlineData("zh-hant", "ZH-HANT")]
    public void A_region_the_provider_knows_is_kept(string toLang, string expected)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBe(expected);
    }

    /// <summary>Android reports Chinese as <c>zh-CN</c> on some devices and <c>zh-HANS</c> on others.</summary>
    [Theory]
    [InlineData("zh-CN", "ZH-HANS")]
    [InlineData("zh-TW", "ZH-HANT")]
    [InlineData("zh-HK", "ZH-HANT")]
    [InlineData("es-MX", "ES-419")]
    public void A_region_the_provider_spells_differently_is_translated(string toLang, string expected)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBe(expected);
    }

    /// <summary>Java's Locale still emits the pre-1989 codes for Hebrew, Indonesian and Yiddish.</summary>
    [Theory]
    [InlineData("iw", "HE")]
    [InlineData("in", "ID")]
    [InlineData("ji", "YI")]
    [InlineData("no", "NB")]
    public void A_legacy_iso_code_still_resolves(string toLang, string expected)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBe(expected);
    }

    /// <summary>
    /// A region the provider has never heard of is still that language: refusing <c>de-AT</c> would
    /// deny an Austrian client a German translation.
    /// </summary>
    [Theory]
    [InlineData("de-AT", "DE")]
    [InlineData("ru-RU", "RU")]
    [InlineData("en-AU", "EN-US")]
    public void An_unknown_region_falls_back_to_the_primary_language(string toLang, string expected)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBe(expected);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("klingon")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_language_the_provider_cannot_reach_resolves_to_nothing(string? toLang)
    {
        TranslationLanguageMap.Resolve(toLang).ShouldBeNull();
    }

    /// <summary>
    /// tdlib's <c>check_tone</c> clears <c>neutral</c> and rejects anything but <c>formal</c> and
    /// <c>casual</c>, so those two are the whole vocabulary. An unknown tone is dropped rather than
    /// refused — the method documents no error for it.
    /// </summary>
    [Theory]
    [InlineData("formal", "prefer_more")]
    [InlineData("casual", "prefer_less")]
    [InlineData("FORMAL", "prefer_more")]
    [InlineData("neutral", null)]
    [InlineData("shakespearean", null)]
    [InlineData(null, null)]
    public void The_tone_becomes_a_formality_preference(string? tone, string? expected)
    {
        TranslationLanguageMap.ResolveFormality(tone).ShouldBe(expected);
    }

    /// <summary>
    /// DeepL 400s when formality is sent for a target that does not support it, so the caller has to be
    /// able to ask. Measured from <c>/v2/languages?type=target</c>: German yes, Russian yes, Ukrainian no.
    /// </summary>
    [Theory]
    [InlineData("DE", true)]
    [InlineData("RU", true)]
    [InlineData("PT-BR", true)]
    [InlineData("UK", false)]
    [InlineData("EN-US", false)]
    public void Only_some_targets_take_a_formality(string target, bool supported)
    {
        TranslationLanguageMap.SupportsFormality(target).ShouldBe(supported);
    }
}
