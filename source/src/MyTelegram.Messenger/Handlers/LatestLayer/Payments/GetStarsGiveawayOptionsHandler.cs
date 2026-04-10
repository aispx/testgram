namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch a list of <a href="https://corefork.telegram.org/api/giveaways#star-giveaways">star giveaway options »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsGiveawayOptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsGiveawayOptionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsGiveawayOptions, TVector<MyTelegram.Schema.IStarsGiveawayOption>>
{
    protected override Task<TVector<MyTelegram.Schema.IStarsGiveawayOption>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsGiveawayOptions obj)
    {
        var options = new TVector<MyTelegram.Schema.IStarsGiveawayOption>
        {
            // Option 1: 500 stars, 5 yearly boosts, $9.99
            new TStarsGiveawayOption
            {
                Default = true,
                Stars = 500,
                YearlyBoosts = 5,
                Currency = "USD",
                Amount = 999,
                Winners = new TVector<MyTelegram.Schema.IStarsGiveawayWinnersOption>
                {
                    new TStarsGiveawayWinnersOption { Default = true, Users = 5, PerUserStars = 100 },
                    new TStarsGiveawayWinnersOption { Users = 10, PerUserStars = 50 },
                    new TStarsGiveawayWinnersOption { Users = 25, PerUserStars = 20 }
                }
            },
            // Option 2: 1000 stars, 10 yearly boosts, $19.99
            new TStarsGiveawayOption
            {
                Stars = 1000,
                YearlyBoosts = 10,
                Currency = "USD",
                Amount = 1999,
                Winners = new TVector<MyTelegram.Schema.IStarsGiveawayWinnersOption>
                {
                    new TStarsGiveawayWinnersOption { Default = true, Users = 10, PerUserStars = 100 },
                    new TStarsGiveawayWinnersOption { Users = 20, PerUserStars = 50 },
                    new TStarsGiveawayWinnersOption { Users = 50, PerUserStars = 20 }
                }
            },
            // Option 3: 2500 stars, 25 yearly boosts, $49.99
            new TStarsGiveawayOption
            {
                Stars = 2500,
                YearlyBoosts = 25,
                Currency = "USD",
                Amount = 4999,
                Winners = new TVector<MyTelegram.Schema.IStarsGiveawayWinnersOption>
                {
                    new TStarsGiveawayWinnersOption { Default = true, Users = 25, PerUserStars = 100 },
                    new TStarsGiveawayWinnersOption { Users = 50, PerUserStars = 50 },
                    new TStarsGiveawayWinnersOption { Users = 125, PerUserStars = 20 }
                }
            },
            // Option 4: 5000 stars, 50 yearly boosts, $99.99 (extended)
            new TStarsGiveawayOption
            {
                Extended = true,
                Stars = 5000,
                YearlyBoosts = 50,
                Currency = "USD",
                Amount = 9999,
                Winners = new TVector<MyTelegram.Schema.IStarsGiveawayWinnersOption>
                {
                    new TStarsGiveawayWinnersOption { Default = true, Users = 50, PerUserStars = 100 },
                    new TStarsGiveawayWinnersOption { Users = 100, PerUserStars = 50 },
                    new TStarsGiveawayWinnersOption { Users = 250, PerUserStars = 20 }
                }
            }
        };

        return Task.FromResult(options);
    }
}