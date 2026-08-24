using MyTelegram.Messenger.Services.Localization;

namespace MyTelegram.Messenger.Tests.Localization;

/// <summary>
/// Feature: localized server texts — the language of PSA and service messages.
///
/// <para>
/// The language comes from the client-reported <c>lang_code</c>, which clients send in many shapes
/// ("ru", "ru-RU", "RU", "en_US") and sometimes not at all. Normalization has to collapse all of
/// those onto a supported language, because <see cref="ServerTexts"/> only has entries for those and
/// an unmapped value would surface as an English text to a Russian user (or vice versa).
/// </para>
/// </summary>
public class ServerLanguageTests
{
    [Theory]
    [InlineData("ru", ServerLanguage.Russian)]
    [InlineData("ru-RU", ServerLanguage.Russian)]
    [InlineData("RU", ServerLanguage.Russian)]
    [InlineData("ru_RU", ServerLanguage.Russian)]
    [InlineData("  ru-ru  ", ServerLanguage.Russian)]
    [InlineData("en", ServerLanguage.English)]
    [InlineData("en-US", ServerLanguage.English)]
    [InlineData("uk", ServerLanguage.Ukrainian)]
    [InlineData("uk-UA", ServerLanguage.Ukrainian)]
    public void Normalizes_regional_and_cased_variants_to_the_base_language(string langCode, string expected)
    {
        ServerLanguage.Normalize(langCode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("de")]
    [InlineData("zh-Hans")]
    public void Falls_back_to_the_default_language_for_missing_or_unsupported_codes(string? langCode)
    {
        ServerLanguage.Normalize(langCode).ShouldBe(ServerLanguage.Default);
    }

    [Fact]
    public void Every_supported_language_has_its_own_psa_text()
    {
        var english = ServerTexts.PremiumLastDayPsa(ServerLanguage.English);
        var russian = ServerTexts.PremiumLastDayPsa(ServerLanguage.Russian);
        var ukrainian = ServerTexts.PremiumLastDayPsa(ServerLanguage.Ukrainian);

        english.ShouldNotBeNullOrWhiteSpace();
        russian.ShouldNotBeNullOrWhiteSpace();
        ukrainian.ShouldNotBeNullOrWhiteSpace();
        russian.ShouldNotBe(english);
        ukrainian.ShouldNotBe(english);
        ukrainian.ShouldNotBe(russian);
    }

    [Fact]
    public void Unknown_language_renders_the_default_language_text()
    {
        ServerTexts.PremiumLastDayPsa("de")
            .ShouldBe(ServerTexts.PremiumLastDayPsa(ServerLanguage.Default));
    }
}
