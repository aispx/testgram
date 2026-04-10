using Microsoft.Extensions.Options;
using MyTelegram.Messenger.Options;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain a list of Telegram Premium <a href="https://corefork.telegram.org/api/giveaways">giveaway/gift code »</a> options.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getPremiumGiftCodeOptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPremiumGiftCodeOptionsHandler(
    IOptions<PremiumOptions> premiumOptions)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetPremiumGiftCodeOptions, TVector<MyTelegram.Schema.IPremiumGiftCodeOption>>
{
    protected override Task<TVector<MyTelegram.Schema.IPremiumGiftCodeOption>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetPremiumGiftCodeOptions obj)
    {
        var opts = premiumOptions.Value;

        var options = new TVector<MyTelegram.Schema.IPremiumGiftCodeOption>
        {
            // USD pricing
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 1,
                Currency = opts.Currency,
                Amount = opts.MonthlyPriceUsd
            },
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 6,
                Currency = opts.Currency,
                Amount = opts.SixMonthPriceUsd
            },
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 12,
                Currency = opts.Currency,
                Amount = opts.YearlyPriceUsd
            },
            // Stars pricing (XTR = Telegram Stars) - matching PremiumPromoConverter
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 1,
                Currency = "XTR",
                Amount = 2500  // 1 month = 2500 stars
            },
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 3,
                Currency = "XTR",
                Amount = 7000  // 3 months = 7000 stars
            },
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 6,
                Currency = "XTR",
                Amount = 13000  // 6 months = 13000 stars
            },
            new TPremiumGiftCodeOption
            {
                Users = 1,
                Months = 12,
                Currency = "XTR",
                Amount = 25000  // 12 months = 25000 stars
            }
        };

        return Task.FromResult(options);
    }
}