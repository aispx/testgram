namespace MyTelegram.Messenger.Options;

public class PremiumOptions
{
    public const string SectionName = "Premium";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Monthly price in cents (e.g., 499 = $4.99)
    /// </summary>
    public int MonthlyPriceUsd { get; set; } = 499;

    /// <summary>
    /// 6-month price in cents (e.g., 2499 = $24.99)
    /// </summary>
    public int SixMonthPriceUsd { get; set; } = 2499;

    /// <summary>
    /// Yearly price in cents (e.g., 3999 = $39.99)
    /// </summary>
    public int YearlyPriceUsd { get; set; } = 3999;

    /// <summary>
    /// Monthly price in Stars (e.g., 2500 = 2500 ⭐)
    /// </summary>
    public int MonthlyPriceStars { get; set; } = 2500;

    /// <summary>
    /// 3-month price in Stars (e.g., 7000 = 7000 ⭐)
    /// </summary>
    public int ThreeMonthPriceStars { get; set; } = 7000;

    /// <summary>
    /// 6-month price in Stars (e.g., 13000 = 13000 ⭐)
    /// </summary>
    public int SixMonthPriceStars { get; set; } = 13000;

    /// <summary>
    /// Yearly price in Stars (e.g., 25000 = 25000 ⭐)
    /// </summary>
    public int YearlyPriceStars { get; set; } = 25000;

    /// <summary>
    /// Currency code (ISO 4217)
    /// </summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Optional bot username for Premium purchases (e.g., @PremiumBot)
    /// </summary>
    public string? BotUsername { get; set; }

    /// <summary>
    /// Optional invoice slug for Premium purchases
    /// </summary>
    public string? InvoiceSlug { get; set; }
}
