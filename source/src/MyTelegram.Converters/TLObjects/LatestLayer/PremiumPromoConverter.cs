using MyTelegram.Schema.Help;

namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class PremiumPromoConverter : IPremiumPromoConverter, ITransientDependency
{

    public virtual int Layer => Layers.LayerLatest;

    public virtual IPremiumPromo ToPremiumPromo()
    {
        // Prices will be read from configuration in GetPremiumPromoHandler
        // This converter just returns the structure
        return new TPremiumPromo
        {
            StatusText =
                "By subscribing to Testgram Premium you agree to the Testgram Terms of Service and Privacy Policy.",
            StatusEntities = new TVector<IMessageEntity>(),
            Users = new TVector<IUser>(),
            VideoSections = new TVector<string>
            {
                "more_upload", "faster_download", "voice_to_text",
                "no_ads", "unique_reactions", "premium_stickers",
                "advanced_chat_management", "profile_badge",
                "animated_userpics", "app_icons"
            },
            Videos = new TVector<IDocument>(),
            PeriodOptions = new TVector<IPremiumSubscriptionOption>
            {
                // USD options (Stripe/Google Play)
                new TPremiumSubscriptionOption
                {
                    Current = true,
                    Amount = 499,
                    Currency = "USD",
                    Months = 1,
                    StoreProduct = "org.telegram.telegramPremium.monthly",
                    BotUrl = string.Empty
                },
                new TPremiumSubscriptionOption
                {
                    Amount = 2499,
                    Currency = "USD",
                    Months = 6,
                    StoreProduct = "org.telegram.telegramPremium.sixmonths",
                    BotUrl = string.Empty
                },
                new TPremiumSubscriptionOption
                {
                    Amount = 3999,
                    Currency = "USD",
                    Months = 12,
                    StoreProduct = "org.telegram.telegramPremium.yearly",
                    BotUrl = string.Empty
                },
                // Stars (XTR) options - with bot URL for invoice billing
                new TPremiumSubscriptionOption
                {
                    Amount = 2500,
                    Currency = "XTR",
                    Months = 1,
                    StoreProduct = string.Empty,
                    BotUrl = "https://t.me/$testgram_premium_1m"
                },
                new TPremiumSubscriptionOption
                {
                    Amount = 7000,
                    Currency = "XTR",
                    Months = 3,
                    StoreProduct = string.Empty,
                    BotUrl = "https://t.me/$testgram_premium_3m"
                },
                new TPremiumSubscriptionOption
                {
                    Amount = 13000,
                    Currency = "XTR",
                    Months = 6,
                    StoreProduct = string.Empty,
                    BotUrl = "https://t.me/$testgram_premium_6m"
                },
                new TPremiumSubscriptionOption
                {
                    Amount = 25000,
                    Currency = "XTR",
                    Months = 12,
                    StoreProduct = string.Empty,
                    BotUrl = "https://t.me/$testgram_premium_12m"
                }
            }
        };
    }
}