using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Phone;

public interface IUserAccessHashKeyCache
{
    Task RememberAsync(long userId, long accessHashKeyId);
    Task<long?> GetAsync(long userId);
}

public sealed class UserAccessHashKeyCache(IConnectionMultiplexer redis) : IUserAccessHashKeyCache, ITransientDependency
{
    private static RedisKey GetKey(long userId) => $"mytelegram:user-access-hash-key:{userId}";

    public Task RememberAsync(long userId, long accessHashKeyId)
    {
        if (userId == 0 || accessHashKeyId == 0)
        {
            return Task.CompletedTask;
        }

        return redis.GetDatabase().StringSetAsync(GetKey(userId), accessHashKeyId, TimeSpan.FromDays(30));
    }

    public async Task<long?> GetAsync(long userId)
    {
        if (userId == 0)
        {
            return null;
        }

        var value = await redis.GetDatabase().StringGetAsync(GetKey(userId));
        if (!value.HasValue || !long.TryParse(value.ToString(), out var accessHashKeyId) || accessHashKeyId == 0)
        {
            return null;
        }

        return accessHashKeyId;
    }
}
