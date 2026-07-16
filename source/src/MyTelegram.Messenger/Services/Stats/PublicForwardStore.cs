using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// MongoDB-backed <see cref="IPublicForwardStore"/> over the <c>stats_public_forward</c> collection.
/// <para>
/// Forwards are deduped on <c>(source, forwardingPeerId, forwardingMsgId)</c> via the document <c>_id</c>,
/// soft-removed via a <c>Removed</c> flag, and paged over a deterministic total order
/// <c>(OrderKey, ForwardingPeerId, ForwardingMsgId)</c> used as an opaque cursor.
/// </para>
/// </summary>
public class PublicForwardStore(IMongoDatabase mongoDatabase) : IPublicForwardStore, ISingletonDependency
{
    private const string CollectionName = "stats_public_forward";
    private const int MaxLimit = 100;

    private IMongoCollection<PublicForwardDocument> Collection =>
        mongoDatabase.GetCollection<PublicForwardDocument>(CollectionName);

    private int _indexEnsured;

    public async Task RecordAsync(ForwardSourceKey source, PublicForwardRecord record)
    {
        await EnsureIndexesAsync();

        var id = BuildId(source, record.ForwardingPeerId, record.ForwardingMsgId);
        var doc = new PublicForwardDocument
        {
            Id = id,
            SourceType = source.Type,
            SourceOwnerPeerId = source.OwnerPeerId,
            SourceItemId = source.ItemId,
            ForwardingPeerId = record.ForwardingPeerId,
            ForwardingMsgId = record.ForwardingMsgId,
            OrderKey = record.OrderKey,
            Removed = false
        };

        // Upsert on the dedupe key so at most one entry exists per (source, forwarding message);
        // re-recording an existing pair refreshes it and clears any prior soft-delete.
        await Collection.ReplaceOneAsync(
            p => p.Id == id,
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task RemoveAsync(ForwardSourceKey source, ForwardRefKey forwardRef)
    {
        await EnsureIndexesAsync();

        var id = BuildId(source, forwardRef.ForwardingPeerId, forwardRef.ForwardingMsgId);
        var update = Builders<PublicForwardDocument>.Update.Set(p => p.Removed, true);
        await Collection.UpdateOneAsync(p => p.Id == id, update);
    }

    public async Task<int> CountAsync(ForwardSourceKey source)
    {
        await EnsureIndexesAsync();

        var count = await Collection.CountDocumentsAsync(BuildSourceFilter(source));
        return (int)count;
    }

    public async Task<PublicForwardPage> GetPageAsync(ForwardSourceKey source, string offset, int limit)
    {
        await EnsureIndexesAsync();

        var count = (int)await Collection.CountDocumentsAsync(BuildSourceFilter(source));

        // limit <= 0 returns an empty page with the true total count (Requirements 6.6, 7.8).
        if (limit <= 0)
        {
            return new PublicForwardPage(count, [], null);
        }

        // Clamp limit to at most 100 (Requirements 6.7).
        var pageSize = Math.Min(limit, MaxLimit);

        var filter = BuildSourceFilter(source);
        if (!string.IsNullOrEmpty(offset))
        {
            filter = Builders<PublicForwardDocument>.Filter.And(filter, BuildCursorFilter(offset));
        }

        var sort = Builders<PublicForwardDocument>.Sort
            .Ascending(p => p.OrderKey)
            .Ascending(p => p.ForwardingPeerId)
            .Ascending(p => p.ForwardingMsgId);

        // Fetch one extra document to determine whether a further page exists.
        var docs = await Collection
            .Find(filter)
            .Sort(sort)
            .Limit(pageSize + 1)
            .ToListAsync();

        var hasMore = docs.Count > pageSize;
        var pageDocs = hasMore ? docs.Take(pageSize).ToList() : docs;

        var items = pageDocs
            .Select(d => new PublicForwardRecord(d.ForwardingPeerId, d.ForwardingMsgId, d.OrderKey))
            .ToList();

        string? nextOffset = null;
        if (hasMore)
        {
            var last = pageDocs[^1];
            nextOffset = BuildCursor(last.OrderKey, last.ForwardingPeerId, last.ForwardingMsgId);
        }

        return new PublicForwardPage(count, items, nextOffset);
    }

    private static string BuildId(ForwardSourceKey source, long fwdPeerId, int fwdMsgId) =>
        $"{(int)source.Type}:{source.OwnerPeerId}:{source.ItemId}:{fwdPeerId}:{fwdMsgId}";

    private static FilterDefinition<PublicForwardDocument> BuildSourceFilter(ForwardSourceKey source)
    {
        var b = Builders<PublicForwardDocument>.Filter;
        return b.And(
            b.Eq(p => p.SourceType, source.Type),
            b.Eq(p => p.SourceOwnerPeerId, source.OwnerPeerId),
            b.Eq(p => p.SourceItemId, source.ItemId),
            b.Eq(p => p.Removed, false));
    }

    private static string BuildCursor(long orderKey, long fwdPeerId, int fwdMsgId) =>
        $"{orderKey}:{fwdPeerId}:{fwdMsgId}";

    private static FilterDefinition<PublicForwardDocument> BuildCursorFilter(string offset)
    {
        var parts = offset.Split(':');
        if (parts.Length != 3
            || !long.TryParse(parts[0], out var orderKey)
            || !long.TryParse(parts[1], out var fwdPeerId)
            || !int.TryParse(parts[2], out var fwdMsgId))
        {
            // Non-empty but unrecognized cursor: signal the handler to reject with an invalid-offset
            // error rather than returning a partial page (Requirements 6.8).
            throw new InvalidStatsOffsetException(offset);
        }

        var b = Builders<PublicForwardDocument>.Filter;

        // (OrderKey, ForwardingPeerId, ForwardingMsgId) > (orderKey, fwdPeerId, fwdMsgId)
        return b.Or(
            b.Gt(p => p.OrderKey, orderKey),
            b.And(
                b.Eq(p => p.OrderKey, orderKey),
                b.Gt(p => p.ForwardingPeerId, fwdPeerId)),
            b.And(
                b.Eq(p => p.OrderKey, orderKey),
                b.Eq(p => p.ForwardingPeerId, fwdPeerId),
                b.Gt(p => p.ForwardingMsgId, fwdMsgId)));
    }

    private async Task EnsureIndexesAsync()
    {
        if (Interlocked.CompareExchange(ref _indexEnsured, 1, 0) != 0)
        {
            return;
        }

        // Index per the data model: {SourceType, SourceOwnerPeerId, SourceItemId, Removed, OrderKey}
        var keys = Builders<PublicForwardDocument>.IndexKeys
            .Ascending(p => p.SourceType)
            .Ascending(p => p.SourceOwnerPeerId)
            .Ascending(p => p.SourceItemId)
            .Ascending(p => p.Removed)
            .Ascending(p => p.OrderKey)
            .Ascending(p => p.ForwardingPeerId)
            .Ascending(p => p.ForwardingMsgId);

        await Collection.Indexes.CreateOneAsync(
            new CreateIndexModel<PublicForwardDocument>(
                keys,
                new CreateIndexOptions { Name = "stats_public_forward_source_order" }));
    }
}
