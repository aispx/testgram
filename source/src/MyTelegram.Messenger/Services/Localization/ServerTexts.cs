namespace MyTelegram.Messenger.Services.Localization;

/// <summary>
/// Server-generated user-facing texts, per language. Every entry must exist for every language in
/// <see cref="ServerLanguage"/>; the lookup falls back to <see cref="ServerLanguage.Default"/>.
/// </summary>
public static class ServerTexts
{
    /// <summary>PSA body served by <c>help.getPromoData</c> for the <c>premium_last_day</c> PSA type.</summary>
    public static string PremiumLastDayPsa(string language) => language switch
    {
        ServerLanguage.Russian => "Сегодня — последняя возможность оплатить Telegram Premium.",
        _ => "Today is the last day to pay for Telegram Premium."
    };

    /// <summary>
    /// Service message sent by the notification service when a bot grants a custom verification
    /// badge. <paramref name="botName"/> is the bot's @username (or "bot &lt;id&gt;"),
    /// <paramref name="iconText"/> is the custom emoji placeholder, and <paramref name="company"/>
    /// is the verifying organisation.
    /// </summary>
    public static string CustomVerificationGranted(string language, string botName, string iconText, string company)
        => language switch
        {
            ServerLanguage.Russian =>
                $"Бот {botName} выдал вам верификацию.\nПредложенный статус:\n{iconText} Аккаунт верифицирован организацией «{company}».",
            _ =>
                $"The bot {botName} has verified you.\nSuggested status:\n{iconText} Account verified by {company}."
        };
}
