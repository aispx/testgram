using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Tests.Payments;

/// <summary>
/// Feature: <c>payments.getBankCardData</c>.
///
/// <para>
/// A card number tapped in a chat (<c>messageEntityBankCard</c>) is resolved to its issuer from a
/// local BIN table, so no card number is ever sent to a third party. Lookup is longest-prefix, with
/// the ISO/IEC 7812 payment networks as the last resort so a valid card always names *something*.
/// See https://corefork.telegram.org/method/payments.getBankCardData
/// </para>
/// </summary>
public class BankCardBinProviderTests
{
    // Luhn-valid numbers built on the BIN each case is about; only the prefix is meaningful.
    [Theory]
    [InlineData("2202200000000008", "Sberbank")]
    [InlineData("4276830000000009", "Sberbank")]
    [InlineData("2200240000000006", "VTB")]
    [InlineData("2200700000000009", "T-Bank")]
    [InlineData("5213240000000004", "T-Bank")]
    [InlineData("2200150000000007", "Alfa-Bank")]
    [InlineData("4242424242424242", "Stripe Test Card")]
    [InlineData("4042430000000001", "Revolut")]
    [InlineData("5351300000000008", "Monzo")]
    [InlineData("371449000000000", "American Express")]
    public void A_known_bin_resolves_to_its_issuer(string cardNumber, string expectedTitle)
    {
        CreateProvider().Resolve(cardNumber)!.Title.ShouldBe(expectedTitle);
    }

    [Fact]
    public void An_unknown_bin_still_names_the_payment_network()
    {
        // Reporting a perfectly valid card as invalid just because its issuer is not on file would be
        // worse than naming the network it was issued on.
        var provider = CreateProvider();

        provider.Resolve("9792000000000003")!.Title.ShouldBe("Troy");
        provider.Resolve("2204000000000000")!.Title.ShouldBe("Mir");
    }

    [Fact]
    public void The_longest_prefix_wins_over_the_network_it_sits_under()
    {
        var provider = CreateProvider();

        // 4... is Visa; both of these sit under it and must beat it on the longer prefix.
        provider.Resolve("4111111111111111")!.Title.ShouldBe("JPMorgan Chase");
        provider.Resolve("4242424242424242")!.Title.ShouldBe("Stripe Test Card");
    }

    [Fact]
    public void An_issuer_the_dataset_never_heard_of_falls_through_to_its_network()
    {
        // 4 is Visa and this BIN has no issuer on file, so Visa is the honest answer.
        CreateProvider().Resolve("4000100000000000")!.Title.ShouldBe("Visa");
    }

    [Theory]
    [InlineData("4242424242424241")]   // fails Luhn
    [InlineData("42424242424")]        // too short
    [InlineData("42424242424242424242")] // too long
    [InlineData("not a card")]
    [InlineData("")]
    [InlineData(null)]
    public void A_number_that_is_not_a_card_resolves_to_nothing(string? cardNumber)
    {
        CreateProvider().Resolve(cardNumber).ShouldBeNull();
    }

    [Fact]
    public void Separators_inside_the_number_are_tolerated()
    {
        var provider = CreateProvider();

        provider.Resolve("4242 4242 4242 4242")!.Title.ShouldBe("Stripe Test Card");
        provider.Resolve("4242-4242-4242-4242")!.Title.ShouldBe("Stripe Test Card");
    }

    [Fact]
    public void An_issuer_url_is_absolute_when_present()
    {
        var entry = CreateProvider().Resolve("2202200000000008")!;

        entry.Url.ShouldNotBeNull();
        entry.Url.ShouldStartWith("https://");
    }

    [Fact]
    public void A_configured_file_replaces_the_built_in_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bank-bins-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"issuers":[["Test Bank","https://example.test"]],"bins":{"4242":0}}""");

        try
        {
            var provider = CreateProvider(path);

            provider.Resolve("4242424242424242")!.Title.ShouldBe("Test Bank");
            // The replacement carries no networks, so an unlisted card no longer resolves at all.
            provider.Resolve("4000100000000000").ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_malformed_file_falls_back_to_the_built_in_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bank-bins-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "not json");

        try
        {
            // Serving an unparsable table would break every card lookup.
            CreateProvider(path).Resolve("4242424242424242")!.Title.ShouldBe("Stripe Test Card");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_falls_back_to_the_built_in_table()
    {
        CreateProvider("/nonexistent/bank-bins.json").Resolve("4242424242424242")!.Title
            .ShouldBe("Stripe Test Card");
    }

    private static IBankCardBinProvider CreateProvider(string? bankBinsFile = null)
    {
        var options = new MyTelegramMessengerServerOptions();
        options.Payments.BankBinsFile = bankBinsFile ?? string.Empty;

        var monitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        monitor.SetupGet(p => p.CurrentValue).Returns(options);

        return new BankCardBinProvider(monitor.Object, NullLogger<BankCardBinProvider>.Instance);
    }
}
