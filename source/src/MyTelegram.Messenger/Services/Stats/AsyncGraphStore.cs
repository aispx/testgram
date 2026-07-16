using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// MongoDB-backed <see cref="IAsyncGraphStore"/> (Async_Graph_Store).
/// <para>Persists issued async graph tokens in the <c>stats_async_graph</c> collection and resolves them,
/// enforcing the fixed precedence of token recognition (<c>GRAPH_INVALID_RELOAD</c>), validity-window
/// expiry (<c>GRAPH_EXPIRED_RELOAD</c>, 86,400 seconds), and snapshot currency
/// (<c>GRAPH_OUTDATED_RELOAD</c>), plus zoom <c>x</c> lookup.</para>
/// </summary>
public sealed class AsyncGraphStore(IMongoDatabase mongoDatabase) : IAsyncGraphStore, ISingletonDependency
{
    /// <summary>The validity window of an issued token, in seconds (Requirement 9.5).</summary>
    public const int ValidityWindowSeconds = 86_400;

    private const string CollectionName = "stats_async_graph";
    private const string CurrentSnapshotCollectionName = "stats_async_graph_current";

    private static readonly JsonSerializerOptions SpecJsonOptions = new()
    {
        // GraphSpec is an immutable record with a single constructor; System.Text.Json binds by
        // constructor parameter name, so property names must round-trip case-insensitively.
        PropertyNameCaseInsensitive = true
    };

    private int _indexesEnsured;

    private IMongoCollection<StatsAsyncGraphDocument> Collection =>
        mongoDatabase.GetCollection<StatsAsyncGraphDocument>(CollectionName);

    private IMongoCollection<StatsAsyncGraphSnapshotDocument> CurrentSnapshotCollection =>
        mongoDatabase.GetCollection<StatsAsyncGraphSnapshotDocument>(CurrentSnapshotCollectionName);

    /// <inheritdoc />
    public async Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(snapshotId);

        await EnsureIndexesAsync();

        var token = GenerateToken();
        var document = new StatsAsyncGraphDocument
        {
            Token = token,
            SnapshotId = snapshotId,
            SpecJson = System.Text.Json.JsonSerializer.Serialize(spec, SpecJsonOptions),
            ZoomJson = zoom is null ? null : System.Text.Json.JsonSerializer.Serialize(zoom, SpecJsonOptions),
            Dark = dark,
            IssuedAt = nowUnix,
            ExpiresAt = DateTime.UnixEpoch.AddSeconds(nowUnix + ValidityWindowSeconds)
        };

        await Collection.ReplaceOneAsync(
            Builders<StatsAsyncGraphDocument>.Filter.Eq(d => d.Token, token),
            document,
            new ReplaceOptions { IsUpsert = true });

        // Issuing a token advances the "current" snapshot pointer for its scope so that tokens issued
        // against an earlier snapshot of the same scope resolve as Outdated (Requirement 9.6).
        var scope = GetSnapshotScope(snapshotId);
        await CurrentSnapshotCollection.ReplaceOneAsync(
            Builders<StatsAsyncGraphSnapshotDocument>.Filter.Eq(d => d.Scope, scope),
            new StatsAsyncGraphSnapshotDocument { Scope = scope, CurrentSnapshotId = snapshotId },
            new ReplaceOptions { IsUpsert = true });

        return token;
    }

    /// <inheritdoc />
    public async Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix)
    {
        // Precedence step 1: token recognition (Requirement 9.4 / 9.9).
        if (string.IsNullOrEmpty(token))
        {
            return Invalid();
        }

        var document = await Collection
            .Find(Builders<StatsAsyncGraphDocument>.Filter.Eq(d => d.Token, token))
            .FirstOrDefaultAsync();

        if (document is null)
        {
            return Invalid();
        }

        // Precedence step 2: validity-window expiry (Requirement 9.5 / 9.9).
        if (document.IssuedAt + ValidityWindowSeconds < nowUnix)
        {
            return new AsyncGraphResolution(AsyncGraphStatus.Expired, null, document.Dark);
        }

        // Precedence step 3: snapshot currency (Requirement 9.6 / 9.9).
        var scope = GetSnapshotScope(document.SnapshotId);
        var current = await CurrentSnapshotCollection
            .Find(Builders<StatsAsyncGraphSnapshotDocument>.Filter.Eq(d => d.Scope, scope))
            .FirstOrDefaultAsync();

        if (current is not null && current.CurrentSnapshotId != document.SnapshotId)
        {
            return new AsyncGraphResolution(AsyncGraphStatus.Outdated, null, document.Dark);
        }

        // Zoom resolution (Requirements 9.3 / 9.8): a non-null x must identify an available zoom series.
        if (x is not null)
        {
            if (string.IsNullOrEmpty(document.ZoomJson))
            {
                return new AsyncGraphResolution(AsyncGraphStatus.ZoomInvalid, null, document.Dark);
            }

            var zoomSpec = DeserializeSpec(document.ZoomJson);
            if (zoomSpec is null)
            {
                return new AsyncGraphResolution(AsyncGraphStatus.ZoomInvalid, null, document.Dark);
            }

            return new AsyncGraphResolution(AsyncGraphStatus.Ok, zoomSpec, document.Dark);
        }

        var spec = DeserializeSpec(document.SpecJson);
        if (spec is null)
        {
            // A stored spec that no longer deserializes is treated as unrecognized.
            return Invalid();
        }

        return new AsyncGraphResolution(AsyncGraphStatus.Ok, spec, document.Dark);
    }

    private async Task EnsureIndexesAsync()
    {
        if (Interlocked.CompareExchange(ref _indexesEnsured, 1, 0) != 0)
        {
            return;
        }

        // TTL index: MongoDB removes documents once ExpiresAt is reached, backing GRAPH_EXPIRED_RELOAD cleanup.
        await Collection.Indexes.CreateOneAsync(new CreateIndexModel<StatsAsyncGraphDocument>(
            Builders<StatsAsyncGraphDocument>.IndexKeys.Ascending(d => d.ExpiresAt),
            new CreateIndexOptions { Name = "stats_async_graph_expires_at_ttl", ExpireAfter = TimeSpan.Zero }));
    }

    private static AsyncGraphResolution Invalid() => new(AsyncGraphStatus.Invalid, null, false);

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static GraphSpec? DeserializeSpec(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<GraphSpec>(json, SpecJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Derives the snapshot scope from a snapshot id. A snapshot id is conventionally
    /// <c>"{scope}:{version}"</c>; the scope is everything before the final <c>':'</c>. A snapshot id
    /// without a delimiter is its own scope, so it is never superseded (never Outdated).
    /// </summary>
    private static string GetSnapshotScope(string snapshotId)
    {
        var index = snapshotId.LastIndexOf(':');
        return index <= 0 ? snapshotId : snapshotId[..index];
    }
}
