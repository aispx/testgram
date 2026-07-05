using System.Reflection;
using StackExchange.Redis;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// Minimal in-memory fake of <see cref="IConnectionMultiplexer"/> / <see cref="IDatabase"/> built with
/// <see cref="DispatchProxy"/> so the push-updates tests can exercise Redis-backed stores (e.g.
/// <c>DeviceLockStore</c>) without a real Redis server.
/// <para>
/// Only the handful of operations the stores actually use are modelled — <c>StringSet</c> (with TTL),
/// <c>KeyExists</c> and <c>KeyDelete</c> — backed by a shared in-memory key set. TTL is accepted but
/// not expired in-process (the round-trip semantics under test depend on explicit set/clear, not on
/// wall-clock expiry). Any other member throws <see cref="NotSupportedException"/> to surface
/// accidental reliance on unmodelled behaviour.
/// </para>
/// </summary>
public static class FakeRedis
{
    /// <summary>Creates an in-memory <see cref="IConnectionMultiplexer"/> backed by a fresh key store.</summary>
    public static IConnectionMultiplexer CreateConnectionMultiplexer()
    {
        var store = new FakeRedisStore();

        var database = DispatchProxy.Create<IDatabase, FakeDatabaseProxy>();
        ((FakeDatabaseProxy)(object)database).Store = store;

        var multiplexer = DispatchProxy.Create<IConnectionMultiplexer, FakeConnectionMultiplexerProxy>();
        ((FakeConnectionMultiplexerProxy)(object)multiplexer).Database = database;

        return multiplexer;
    }
}

/// <summary>Shared in-memory key store. Presence of a key models an active (un-expired) entry.</summary>
public sealed class FakeRedisStore
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Set(string key)
    {
        lock (_gate)
        {
            _keys.Add(key);
        }
    }

    public bool Exists(string key)
    {
        lock (_gate)
        {
            return _keys.Contains(key);
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            return _keys.Remove(key);
        }
    }
}

public class FakeConnectionMultiplexerProxy : DispatchProxy
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
                    $"FakeConnectionMultiplexer does not implement '{targetMethod?.Name}'.");
        }
    }
}

public class FakeDatabaseProxy : DispatchProxy
{
    public FakeRedisStore Store { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? string.Empty;

        switch (name)
        {
            case nameof(IDatabaseAsync.StringSetAsync):
            {
                Store.Set(KeyOf(args));
                return Task.FromResult(true);
            }

            case nameof(IDatabaseAsync.KeyExistsAsync):
            {
                return Task.FromResult(Store.Exists(KeyOf(args)));
            }

            case nameof(IDatabaseAsync.KeyDeleteAsync):
            {
                var removed = Store.Delete(KeyOf(args));
                return Task.FromResult(removed);
            }

            // Synchronous variants, in case a store ever calls them.
            case nameof(IDatabase.StringSet):
            {
                Store.Set(KeyOf(args));
                return true;
            }

            case nameof(IDatabase.KeyExists):
            {
                return Store.Exists(KeyOf(args));
            }

            case nameof(IDatabase.KeyDelete):
            {
                return Store.Delete(KeyOf(args));
            }

            default:
                throw new NotSupportedException(
                    $"FakeDatabase does not implement '{name}'.");
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
}
