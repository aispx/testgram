namespace MyTelegram.Messenger.Handlers;

/// <summary>
/// Returns a set of future server salts, so the client never has to stall waiting for the
/// current salt to rotate.
/// <para><c>See <a href="https://corefork.telegram.org/method/get_future_salts"/> </c></para>
/// <para><c>See <a href="https://corefork.telegram.org/api/optimisation"/> </c></para>
/// </summary>
/// <remarks>
/// This is an MTProto service query, not an API method: the reply is a bare
/// <c>future_salts</c> (which carries its own <c>req_msg_id</c>), not an <c>rpc_result</c>.
/// That is why this derives from <see cref="BaseObjectHandler{TInput,TOutput}"/> like
/// <c>PingHandler</c>, rather than from <c>RpcResultObjectHandler</c>.
/// <para>
/// The generated salts are persisted to Redis under the pre-existing
/// <see cref="FutureSaltCacheItem"/> key so that the session layer, which stamps and validates
/// <c>server_salt</c> on every encrypted message, can resolve any salt the client starts using.
/// </para>
/// </remarks>
internal sealed class GetFutureSaltsHandler(
    ICacheManager<List<FutureSaltCacheItem>> cacheManager,
    IRandomHelper randomHelper)
    : BaseObjectHandler<RequestGetFutureSalts, IFutureSalts>
{
    /// <summary>Telegram never hands out more than 64 salts in one reply.</summary>
    private const int MaxSaltCount = 64;

    /// <summary>Each salt is valid for an hour...</summary>
    private const int SaltValiditySeconds = 3600;

    /// <summary>...and a new one starts every half hour, so consecutive salts overlap by 30 minutes.</summary>
    private const int SaltIntervalSeconds = 1800;

    protected override async Task<IFutureSalts> HandleCoreAsync(IRequestInput input, RequestGetFutureSalts obj)
    {
        var num = Math.Clamp(obj.Num, 1, MaxSaltCount);
        var now = CurrentDate;
        var cacheKey = FutureSaltCacheItem.GetCacheKey(input.AuthKeyId);

        // Keep only salts that have not expired yet. Salts already handed out must be preserved
        // verbatim: the client may still be stamping messages with one of them, and the session
        // layer resolves the incoming server_salt against exactly this list.
        var salts = await cacheManager.GetAsync(cacheKey) ?? [];
        salts = [.. salts.Where(p => p.ValidUntil > now).OrderBy(p => p.ValidSince)];

        var generated = false;
        while (salts.Count < num)
        {
            // Chain the next window off the last one we have, or off the current half-hour
            // boundary when the list is empty, so the windows stay aligned and contiguous.
            var validSince = salts.Count == 0
                ? now - (now % SaltIntervalSeconds)
                : salts[^1].ValidSince + SaltIntervalSeconds;

            salts.Add(new FutureSaltCacheItem(randomHelper.NextInt64(), validSince,
                validSince + SaltValiditySeconds));
            generated = true;
        }

        if (generated)
        {
            // Expire the cache entry only once every salt in it is dead.
            var ttl = salts[^1].ValidUntil - now + SaltIntervalSeconds;
            await cacheManager.SetAsync(cacheKey, salts, ttl);
        }

        var result = new TVector<IFutureSalt>();
        foreach (var salt in salts.Take(num))
        {
            result.Add(new TFutureSalt
            {
                Salt = salt.Salt,
                ValidSince = salt.ValidSince,
                ValidUntil = salt.ValidUntil
            });
        }

        return new TFutureSalts
        {
            ReqMsgId = input.ReqMsgId,
            Now = now,
            Salts = result
        };
    }
}
