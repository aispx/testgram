using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Localization;
using MyTelegram.Messenger.Services.StarsSubscriptions;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Localization;

/// <summary>
/// Feature: the service notifications (user 777000) about channel Star subscriptions.
///
/// <para>
/// The texts carry the renewal date and the price, and the low balance warning carries the Star
/// top-up deep links the official clients understand. A wrong date format, a locale-dependent one,
/// or a malformed <c>tg://stars_topup</c> link all leave the user with a message they cannot act
/// on, so both are pinned down here.
/// </para>
/// </summary>
public class StarsSubscriptionNotificationTests
{
    /// <summary>2024-01-01 00:00:00 UTC.</summary>
    private const int NewYear2024 = 1704067200;

    private static StarsSubscriptionDocument Subscription(long amount = 100,
        int period = ChatInviteSubscriptionPricing.MonthlyPeriod) => new()
    {
        Id = "2010001-1000001",
        UserId = 2010001,
        PeerId = 1000001,
        InviteHash = "abcdef",
        Period = period,
        Amount = amount,
        UntilDate = NewYear2024
    };

    [Theory]
    [InlineData(ServerLanguage.English)]
    [InlineData(ServerLanguage.Russian)]
    [InlineData(ServerLanguage.Ukrainian)]
    public void Renewal_texts_state_the_date_in_utc_and_in_a_locale_independent_format(string language)
    {
        // The timestamp is rendered against InvariantCulture, so every language gets the same
        // dd/MM/yyyy the clients show, whatever culture the server process runs under.
        var extended = ServerTexts.StarsSubscriptionExtended(language, "My channel", 100, NewYear2024);
        var warning = ServerTexts.StarsSubscriptionLowBalance(language, "My channel", NewYear2024, 5, 100);

        extended.ShouldContain("01/01/2024");
        extended.ShouldContain("00:00:00");
        extended.ShouldContain("UTC");
        warning.ShouldContain("01/01/2024");
        warning.ShouldContain("00:00:00");
    }

    [Theory]
    [InlineData(ServerLanguage.English)]
    [InlineData(ServerLanguage.Russian)]
    [InlineData(ServerLanguage.Ukrainian)]
    public void Renewal_texts_name_the_channel_and_the_amounts(string language)
    {
        ServerTexts.StarsSubscriptionExtended(language, "My channel", 100, NewYear2024)
            .ShouldContain("My channel");

        var warning = ServerTexts.StarsSubscriptionLowBalance(language, "My channel", NewYear2024, 5, 100);
        warning.ShouldContain("My channel");
        warning.ShouldContain("5 ⭐");
        warning.ShouldContain("100 ⭐");

        ServerTexts.StarsSubscriptionExtendFailed(language, "My channel", 100)
            .ShouldContain("100 ⭐");
    }

    [Fact]
    public void Unsupported_language_falls_back_to_the_default_text()
    {
        ServerTexts.StarsSubscriptionExtendFailed("de", "My channel", 100)
            .ShouldBe(ServerTexts.StarsSubscriptionExtendFailed(ServerLanguage.Default, "My channel", 100));
    }

    [Fact]
    public void Topup_buttons_ask_for_one_period_and_for_twelve_of_them()
    {
        var markup = StarsSubscriptionRenewalService.BuildTopupMarkup(ServerLanguage.English, Subscription());

        var buttons = ButtonsOf(markup);
        buttons.Count.ShouldBe(2);
        buttons[0].Url.ShouldBe("tg://stars_topup?balance=100&purpose=subs");
        buttons[1].Url.ShouldBe("tg://stars_topup?balance=1200&purpose=subadvance12");
        buttons.ShouldAllBe(p => p.Text.Length > 0);
    }

    [Fact]
    public void A_non_monthly_period_gets_no_twelve_months_button()
    {
        // "12 months" is priced as twelve monthly periods; for any other period there is no such
        // price to offer, so only the plain top-up button is shown.
        var markup = StarsSubscriptionRenewalService.BuildTopupMarkup(ServerLanguage.English,
            Subscription(period: ChatInviteSubscriptionPricing.MonthlyPeriod / 2));

        var buttons = ButtonsOf(markup);
        buttons.Count.ShouldBe(1);
        buttons[0].Url.ShouldBe("tg://stars_topup?balance=100&purpose=subs");
    }

    [Fact]
    public void Button_labels_follow_the_users_language()
    {
        var english = ButtonsOf(StarsSubscriptionRenewalService.BuildTopupMarkup(ServerLanguage.English,
            Subscription()));
        var russian = ButtonsOf(StarsSubscriptionRenewalService.BuildTopupMarkup(ServerLanguage.Russian,
            Subscription()));

        russian[0].Text.ShouldNotBe(english[0].Text);
        russian[0].Url.ShouldBe(english[0].Url);
    }

    private static List<TKeyboardButtonUrl> ButtonsOf(IReplyMarkup markup)
    {
        var inline = markup.ShouldBeOfType<TReplyInlineMarkup>();

        return inline.Rows
            .Cast<TKeyboardButtonRow>()
            .SelectMany(p => p.Buttons)
            .Cast<TKeyboardButtonUrl>()
            .ToList();
    }
}
