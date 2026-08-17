using System.Globalization;

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
        ServerLanguage.Ukrainian => "Сьогодні — останній день, щоб оплатити Telegram Premium.",
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
            ServerLanguage.Ukrainian =>
                $"Бот {botName} видав вам верифікацію.\nЗапропонований статус:\n{iconText} Акаунт верифіковано організацією «{company}».",
            _ =>
                $"The bot {botName} has verified you.\nSuggested status:\n{iconText} Account verified by {company}."
        };

    /// <summary>
    /// Sent by the notification service after a channel Star subscription was renewed and the
    /// stars were charged. <paramref name="nextDate"/> is when the next renewal is due.
    /// </summary>
    public static string StarsSubscriptionExtended(string language, string channelTitle, long amount, int nextDate)
    {
        var date = FormatDate(nextDate);
        var time = FormatTime(nextDate);

        return language switch
        {
            ServerLanguage.Russian =>
                $"Подписка на канал «{channelTitle}» продлена за {amount} ⭐.\n" +
                $"Следующее продление — {date} в {time} UTC.",
            ServerLanguage.Ukrainian =>
                $"Підписку на канал «{channelTitle}» продовжено за {amount} ⭐.\n" +
                $"Наступне продовження — {date} о {time} UTC.",
            _ =>
                $"Subscription to channel «{channelTitle}» was extended with a payment of {amount} ⭐.\n" +
                $"Next extension date is on {date} at {time} UTC."
        };
    }

    /// <summary>
    /// Sent ahead of a renewal whose price the user's Star balance does not cover, together with
    /// the Star top-up buttons built by <see cref="StarsTopupButton"/>.
    /// </summary>
    public static string StarsSubscriptionLowBalance(string language,
        string channelTitle,
        int extendDate,
        long currentBalance,
        long requiredAmount)
    {
        var date = FormatDate(extendDate);
        var time = FormatTime(extendDate);

        return language switch
        {
            ServerLanguage.Russian =>
                $"Подписка на канал «{channelTitle}» продлевается {date} в {time} UTC.\n" +
                $"Ваш текущий баланс — {currentBalance} ⭐, этого недостаточно.\n" +
                $"Пополните баланс на {requiredAmount} ⭐ или больше с помощью кнопки ниже, чтобы сохранить подписку на канал «{channelTitle}».",
            ServerLanguage.Ukrainian =>
                $"Підписка на канал «{channelTitle}» продовжується {date} о {time} UTC.\n" +
                $"Ваш поточний баланс — {currentBalance} ⭐, цього недостатньо.\n" +
                $"Поповніть баланс на {requiredAmount} ⭐ або більше за допомогою кнопки нижче, щоб зберегти підписку на канал «{channelTitle}».",
            _ =>
                $"Subscription to channel «{channelTitle}» is extending on {date} at {time} UTC.\n" +
                $"Your current balance is {currentBalance} ⭐, which is below needed.\n" +
                $"Please top up your balance for {requiredAmount} ⭐ or more using the button below to continue enjoying your subscription to channel «{channelTitle}»."
        };
    }

    /// <summary>
    /// Sent when a renewal was due but the Star balance still did not cover it.
    /// </summary>
    public static string StarsSubscriptionExtendFailed(string language, string channelTitle, long amount)
        => language switch
        {
            ServerLanguage.Russian =>
                $"Подписку на канал «{channelTitle}» не удалось продлить, потому что на вашем балансе меньше {amount} ⭐.",
            ServerLanguage.Ukrainian =>
                $"Підписку на канал «{channelTitle}» не вдалося продовжити, бо на вашому балансі менше {amount} ⭐.",
            _ =>
                $"Subscription to channel «{channelTitle}» couldn't be extended because your balance is below {amount} ⭐."
        };

    /// <summary>Label of the Star top-up button on the low balance warning.</summary>
    public static string StarsTopupButton(string language) => language switch
    {
        ServerLanguage.Russian => "Пополнить баланс звёзд",
        ServerLanguage.Ukrainian => "Поповнити баланс зірок",
        _ => "Top up your Stars Balance"
    };

    /// <summary>Label of the button that tops up enough stars for the next twelve periods.</summary>
    public static string StarsTopupYearButton(string language) => language switch
    {
        ServerLanguage.Russian => "Пополнить на следующие 12 месяцев",
        ServerLanguage.Ukrainian => "Поповнити на наступні 12 місяців",
        _ => "Top up for the next 12 months"
    };

    private static string FormatDate(int unixTime) => DateTimeOffset.FromUnixTimeSeconds(unixTime)
        .UtcDateTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string FormatTime(int unixTime) => DateTimeOffset.FromUnixTimeSeconds(unixTime)
        .UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
