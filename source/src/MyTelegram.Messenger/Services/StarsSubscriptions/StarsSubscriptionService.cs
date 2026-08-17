using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;

namespace MyTelegram.Messenger.Services.StarsSubscriptions;

/// <summary>
/// A user's Telegram Star subscription to a channel, bought through a paid invite link.
/// See https://corefork.telegram.org/api/invites#paid-invite-links
/// </summary>
public class StarsSubscriptionDocument
{
    /// <summary>
    /// <c>{userId}-{peerId}</c>: a user holds at most one subscription per channel.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }
    public long PeerId { get; set; }
    public string InviteHash { get; set; } = string.Empty;

    /// <summary>Billing period in seconds.</summary>
    public int Period { get; set; }

    /// <summary>Stars charged per <see cref="Period"/>.</summary>
    public long Amount { get; set; }

    /// <summary>When the currently paid-for period ends.</summary>
    public int UntilDate { get; set; }

    /// <summary>Set when the user turned off auto-renewal; the current period still runs out.</summary>
    public bool Canceled { get; set; }

    /// <summary>Set when the channel admin, rather than the user, ended the subscription.</summary>
    public bool BotCanceled { get; set; }

    /// <summary>First purchase date.</summary>
    public int Date { get; set; }
}

public interface IStarsSubscriptionService
{
    /// <summary>
    /// The user's subscription to <paramref name="peerId"/> whose paid-for period has not run out
    /// yet, or null. Cancelled subscriptions stay active until <see cref="StarsSubscriptionDocument.UntilDate"/>.
    /// </summary>
    Task<StarsSubscriptionDocument?> GetActiveSubscriptionAsync(long userId, long peerId);

    Task<StarsSubscriptionDocument?> GetSubscriptionAsync(long userId, long peerId);

    Task<StarsSubscriptionDocument?> GetSubscriptionByIdAsync(long userId, string subscriptionId);

    Task<IReadOnlyList<StarsSubscriptionDocument>> GetSubscriptionsAsync(long userId);

    /// <summary>
    /// Charges one period: debits the buyer's stars, credits the channel's revenue balance, writes
    /// the ledger entry and extends the subscription. Returns null when the balance is too low, in
    /// which case nothing was moved.
    /// </summary>
    Task<StarsSubscriptionDocument?> ChargeAsync(long userId,
        long peerId,
        string inviteHash,
        int period,
        long amount,
        string? channelTitle);

    Task SetCanceledAsync(long userId, string subscriptionId, bool canceled);
}

public class StarsSubscriptionService(IMongoDatabase database) : IStarsSubscriptionService, ITransientDependency
{
    public const string CollectionName = "star-subscriptions";

    private IMongoCollection<StarsSubscriptionDocument> Collection =>
        database.GetCollection<StarsSubscriptionDocument>(CollectionName);

    private static string BuildId(long userId, long peerId) => $"{userId}-{peerId}";

    public async Task<StarsSubscriptionDocument?> GetActiveSubscriptionAsync(long userId, long peerId)
    {
        var subscription = await GetSubscriptionAsync(userId, peerId);

        return subscription is { } s && s.UntilDate > DateTime.UtcNow.ToTimestamp() ? s : null;
    }

    public Task<StarsSubscriptionDocument?> GetSubscriptionAsync(long userId, long peerId)
    {
        return Collection.Find(x => x.Id == BuildId(userId, peerId)).FirstOrDefaultAsync()!;
    }

    public Task<StarsSubscriptionDocument?> GetSubscriptionByIdAsync(long userId, string subscriptionId)
    {
        return Collection.Find(x => x.Id == subscriptionId && x.UserId == userId).FirstOrDefaultAsync()!;
    }

    public async Task<IReadOnlyList<StarsSubscriptionDocument>> GetSubscriptionsAsync(long userId)
    {
        return await Collection.Find(x => x.UserId == userId).SortByDescending(x => x.UntilDate).ToListAsync();
    }

    public async Task<StarsSubscriptionDocument?> ChargeAsync(long userId,
        long peerId,
        string inviteHash,
        int period,
        long amount,
        string? channelTitle)
    {
        // Debit first: TryDebitAsync only succeeds when the balance already covers the amount, so a
        // failure here means nothing has moved and the caller can abort cleanly.
        if (!await StarsBalanceHelper.TryDebitAsync(database, userId, amount))
        {
            return null;
        }

        await ChannelRevenueHelper.CreditAsync(database,
            peerId,
            ChannelRevenueHelper.StarsCurrency,
            amount,
            sourceUserId: userId,
            title: channelTitle,
            description: "Channel subscription");

        await StarsBalanceHelper.AddTransactionAsync(database,
            userId,
            -amount,
            peerChannelId: peerId,
            title: channelTitle,
            description: "Channel subscription");

        var now = DateTime.UtcNow.ToTimestamp();
        var existing = await GetSubscriptionAsync(userId, peerId);

        // Renewing before the current period runs out extends it; renewing afterwards (or buying
        // for the first time) starts a fresh period from now.
        var untilDate = existing is { } e && e.UntilDate > now
            ? e.UntilDate + period
            : now + period;

        var document = new StarsSubscriptionDocument
        {
            Id = BuildId(userId, peerId),
            UserId = userId,
            PeerId = peerId,
            InviteHash = inviteHash,
            Period = period,
            Amount = amount,
            UntilDate = untilDate,
            Canceled = false,
            BotCanceled = false,
            Date = existing?.Date ?? now
        };

        await Collection.ReplaceOneAsync(x => x.Id == document.Id, document, new ReplaceOptions { IsUpsert = true });

        return document;
    }

    public Task SetCanceledAsync(long userId, string subscriptionId, bool canceled)
    {
        return Collection.UpdateOneAsync(
            x => x.Id == subscriptionId && x.UserId == userId,
            Builders<StarsSubscriptionDocument>.Update.Set(x => x.Canceled, canceled));
    }
}
