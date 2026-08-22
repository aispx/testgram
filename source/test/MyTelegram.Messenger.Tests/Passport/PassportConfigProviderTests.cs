using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: <c>help.getPassportConfig</c>.
///
/// <para>
/// The table maps a country code to the language its passport form should be filled in. tdlib parses
/// it by searching for <c>"CC":"</c> in the raw string, so it has to be a flat JSON object; a client
/// that already has the current table sends its hash back and gets
/// <c>help.passportConfigNotModified</c>.
/// See https://corefork.telegram.org/constructor/help.passportConfig
/// </para>
/// </summary>
public class PassportConfigProviderTests
{
    [Fact]
    public void The_built_in_table_is_a_flat_json_object_of_country_codes()
    {
        var provider = CreateProvider();

        using var document = JsonDocument.Parse(provider.GetCountriesLangs());

        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            property.Name.Length.ShouldBe(2);
            property.Value.ValueKind.ShouldBe(JsonValueKind.String);
            property.Value.GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_table_is_searchable_the_way_tdlib_searches_it()
    {
        // tdlib does data.find("\"" + country_code + "\":\"") on the raw string (SecureManager.cpp), so
        // a space after the colon makes every lookup miss.
        var provider = CreateProvider();

        provider.GetCountriesLangs().ShouldContain("\"RU\":\"ru\"");
        provider.GetCountriesLangs().ShouldNotContain("\": \"");
    }

    [Fact]
    public void The_hash_is_stable_and_never_zero()
    {
        var provider = CreateProvider();

        // 0 is what a client sends when it has nothing cached, so producing it would answer
        // passportConfigNotModified to a client that has no config at all.
        provider.GetHash().ShouldNotBe(0);
        provider.GetHash().ShouldBe(provider.GetHash());
    }

    [Fact]
    public void A_configured_file_replaces_the_built_in_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"passport-langs-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"ZZ":"zz"}""");

        try
        {
            var provider = CreateProvider(path);

            provider.GetCountriesLangs().ShouldContain("\"ZZ\"");
            provider.GetHash().ShouldNotBe(CreateProvider().GetHash());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_malformed_file_falls_back_to_the_built_in_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"passport-langs-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "not json");

        try
        {
            // Forwarding an unparsable table would break the passport form for every user.
            CreateProvider(path).GetCountriesLangs().ShouldBe(CreateProvider().GetCountriesLangs());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_falls_back_to_the_built_in_table()
    {
        CreateProvider("/nonexistent/passport-langs.json").GetCountriesLangs()
            .ShouldBe(CreateProvider().GetCountriesLangs());
    }

    private static IPassportConfigProvider CreateProvider(string? countriesLangsFile = null)
    {
        var options = new MyTelegramMessengerServerOptions();
        options.Passport.CountriesLangsFile = countriesLangsFile ?? string.Empty;

        var monitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        monitor.SetupGet(p => p.CurrentValue).Returns(options);

        return new PassportConfigProvider(monitor.Object, NullLogger<PassportConfigProvider>.Instance);
    }
}
