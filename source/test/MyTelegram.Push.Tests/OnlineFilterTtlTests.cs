// Feature: push-updates, INTEGRATION 7.4: онлайн-фильтр TTL — MarkOnlineAsync затем
// IsOnlineAsync == true; после истечения TTL (90с) → false.
//
// Req 7.4 states: WHEN incoming MTProto traffic reaches a PermAuthKeyId, THE Фильтр_Онлайн SHALL
// mark that PermAuthKeyId as online with a bounded time-to-live on the mark. This integration test
// exercises the PRODUCTION PushOnlineFilter (MyTelegram.Messenger.QueryServer.Services) end-to-end:
//   1. MarkOnlineAsync(authKeyId)  =>  IsOnlineAsync(authKeyId) == true   (the round-trip);
//   2. after the TTL elapses        =>  IsOnlineAsync(authKeyId) == false  (the mark is bounded).
//
// TTL-expiry approach
// -------------------
// PushOnlineFilter hard-codes a 90-second TTL (OnlineTtlSeconds) on the Redis key push:online:{id:x}.
// Two things make a naive test impractical:
//   * a real Redis server is not guaranteed to be reachable in this environment;
//   * waiting 90 real seconds for wall-clock expiry is too slow and flaky for CI.
// The shared Infrastructure.FakeRedis used by Property30/Property31 deliberately accepts the TTL
// argument but does NOT expire keys (its round-trips depend on explicit set/clear, not the clock).
//
// So this test uses a dedicated TTL-AWARE in-memory IConnectionMultiplexer (built with DispatchProxy,
// the same technique as FakeRedis/ThrowingRedis) driven by a controllable virtual clock. StringSetAsync
// records the key's absolute expiry as (virtual-now + ttl); KeyExistsAsync reports the key present only
// while virtual-now < expiry, dropping it once expired. The test then advances the virtual clock past
// 90 seconds to deterministically simulate TTL expiry — no real Redis and no real delay required. This
// faithfully models Redis's own TTL semantics against the unmodified production filter.
//
// Validates: Requirements 7.4

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.QueryServer.Services;
using Shouldly;
using StackExchange.Redis;

namespace MyTelegram.Push.Tests;

public class OnlineFilterTtlTests
{
    /// <summary>The TTL (seconds) PushOnlineFilter applies to an online mark. Mirrors the filter's
    /// private <c>OnlineTtlSeconds</c>; used only to drive the virtual clock past expiry.</summary>
    private const int OnlineTtlSeconds = 90;

    // INTEGRATION 7.4: MarkOnlineAsync => online; after TTL expiry => offline.
    // Validates: Requirements 7.4
    [Fact]
    public void MarkOnline_makes_device_online_then_offline_after_ttl_expiry()
    {
        // Arrange: the production filter over a TTL-aware in-memory Redis with a virtual clock.
        var clock = new VirtualClock(DateTimeOffset.UnixEpoch);
        var redis = TtlRedis.CreateConnectionMultiplexer(clock);
        var filter = new PushOnlineFilter(redis, NullLogger<PushOnlineFilter>.Instance);

        const long authKeyId = 0x0123_4567_89AB_CDEFL;

        // A fresh auth key has never been marked => offline.
        filter.IsOnlineAsync(authKeyId).GetAwaiter().GetResult()
            .ShouldBeFalse("an auth key that was never marked must be reported offline");

        // Act 1: incoming traffic marks the auth key online.
        filter.MarkOnlineAsync(authKeyId).GetAwaiter().GetResult();

        // Assert 1 (round-trip): immediately after marking, the auth key is online.
        filter.IsOnlineAsync(authKeyId).GetAwaiter().GetResult()
            .ShouldBeTrue("right after MarkOnlineAsync the auth key must be reported online");

        // Still online just before the TTL boundary (89s < 90s).
        clock.Advance(TimeSpan.FromSeconds(OnlineTtlSeconds - 1));
        filter.IsOnlineAsync(authKeyId).GetAwaiter().GetResult()
            .ShouldBeTrue("the mark must still be live one second before the TTL elapses");

        // Act 2: advance the virtual clock past the 90s TTL.
        clock.Advance(TimeSpan.FromSeconds(2));

        // Assert 2 (bounded TTL): once the TTL has elapsed, the mark is gone => offline.
        filter.IsOnlineAsync(authKeyId).GetAwaiter().GetResult()
            .ShouldBeFalse("after the TTL elapses the online mark must expire and report offline");
    }

    /// <summary>A manually advanced clock so TTL expiry is simulated deterministically without waiting.</summary>
    public sealed class VirtualClock(DateTimeOffset start)
    {
        private DateTimeOffset _now = start;
        private readonly object _gate = new();

        public DateTimeOffset Now
        {
            get { lock (_gate) { return _now; } }
        }

        public void Advance(TimeSpan delta)
        {
            lock (_gate) { _now += delta; }
        }
    }

    /// <summary>
    /// Builds a TTL-aware in-memory <see cref="IConnectionMultiplexer"/> using <see cref="DispatchProxy"/>
    /// (same approach as Infrastructure.FakeRedis), but unlike FakeRedis it honours key expiry against a
    /// supplied <see cref="VirtualClock"/>. Only the operations PushOnlineFilter uses are modelled:
    /// <c>GetDatabase</c>, <c>StringSetAsync</c> (with TTL) and <c>KeyExistsAsync</c>.
    /// </summary>
    private static class TtlRedis
    {
        public static IConnectionMultiplexer CreateConnectionMultiplexer(VirtualClock clock)
        {
            var store = new TtlStore(clock);

            var database = DispatchProxy.Create<IDatabase, TtlDatabaseProxy>();
            ((TtlDatabaseProxy)(object)database).Store = store;

            var multiplexer = DispatchProxy.Create<IConnectionMultiplexer, TtlConnectionProxy>();
            ((TtlConnectionProxy)(object)multiplexer).Database = database;

            return multiplexer;
        }
    }

    /// <summary>In-memory key store that expires keys against a <see cref="VirtualClock"/>.</summary>
    public sealed class TtlStore(VirtualClock clock)
    {
        private readonly Dictionary<string, DateTimeOffset?> _expiries = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        /// <summary>Sets a key with an optional TTL; expiry is recorded as virtual-now + ttl.</summary>
        public void Set(string key, TimeSpan? ttl)
        {
            lock (_gate)
            {
                _expiries[key] = ttl.HasValue ? clock.Now + ttl.Value : (DateTimeOffset?)null;
            }
        }

        /// <summary>Reports whether a key is present and not yet expired (dropping it if expired).</summary>
        public bool Exists(string key)
        {
            lock (_gate)
            {
                if (!_expiries.TryGetValue(key, out var expiry))
                {
                    return false;
                }

                if (expiry.HasValue && clock.Now >= expiry.Value)
                {
                    _expiries.Remove(key);
                    return false;
                }

                return true;
            }
        }
    }

    public class TtlConnectionProxy : DispatchProxy
    {
        public IDatabase Database { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IConnectionMultiplexer.GetDatabase):
                    return Database;
                case "get_IsConnected":
                    return true;
                default:
                    throw new NotSupportedException(
                        $"TtlConnection does not implement '{targetMethod?.Name}'.");
            }
        }
    }

    public class TtlDatabaseProxy : DispatchProxy
    {
        public TtlStore Store { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name ?? string.Empty;

            switch (name)
            {
                case nameof(IDatabaseAsync.StringSetAsync):
                {
                    Store.Set(KeyOf(args), TtlOf(args));
                    return Task.FromResult(true);
                }

                case nameof(IDatabaseAsync.KeyExistsAsync):
                {
                    return Task.FromResult(Store.Exists(KeyOf(args)));
                }

                default:
                    throw new NotSupportedException(
                        $"TtlDatabase does not implement '{name}'.");
            }
        }

        private static string KeyOf(object?[]? args)
        {
            if (args is null || args.Length == 0 || args[0] is null)
            {
                throw new ArgumentException("Expected a RedisKey as the first argument.");
            }

            // RedisKey is a struct with a string conversion via ToString().
            return args[0]!.ToString()!;
        }

        /// <summary>Extracts the TTL argument (TimeSpan?) StackExchange.Redis passes to StringSetAsync.</summary>
        private static TimeSpan? TtlOf(object?[]? args)
        {
            if (args is null)
            {
                return null;
            }

            foreach (var arg in args)
            {
                if (arg is TimeSpan ts)
                {
                    return ts;
                }
            }

            return null;
        }
    }
}
