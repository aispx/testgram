using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using EventFlow.Core;
using MyTelegram.Schema;

namespace MyTelegram.Services.Services;

public class InvokeAfterMsgProcessor(IHandlerHelper handlerHelper, ILogger<InvokeAfterMsgProcessor> logger) : IInvokeAfterMsgProcessor
    , ISingletonDependency
{
    private readonly CircularBuffer<long> _recentMessageIds = new(50000);
    private readonly ConcurrentDictionary<long, InvokeAfterMsgItem> _requests = new();
    private readonly System.Threading.Channels.Channel<long> _completedReqMsgIds = Channel.CreateUnbounded<long>();

    // Maps each still-pending dependency message id -> the multi-wait items blocked on it.
    // A single MultiWaitItem is registered under every one of its remaining dependency ids so
    // that whichever id completes last can find and release it.
    private readonly ConcurrentDictionary<long, ConcurrentBag<MultiWaitItem>> _multiWaits = new();

    public void AddToRecentMessageIdList(long messageId)
    {
        _recentMessageIds.Put(messageId);
    }

    public bool ExistsInRecentMessageId(long messageId)
    {
        return _recentMessageIds.Contains(messageId);
    }

    public void Enqueue(long reqMsgId,
        IRequestInput input,
        IObject query)
    {
        _requests.TryAdd(reqMsgId, new InvokeAfterMsgItem(input, query));
    }

    public void EnqueueAfterMsgs(IReadOnlyList<long> dependencyMsgIds,
        IRequestInput input,
        IObject query)
    {
        // No dependencies to wait on: run immediately (defensive; the handler normally only
        // enqueues when at least one id is still pending).
        if (dependencyMsgIds.Count == 0)
        {
            var immediate = new MultiWaitItem(input, query, dependencyMsgIds);
            if (immediate.TryMarkExecuted())
            {
                ExecuteMultiWaitItem(immediate);
            }

            return;
        }

        var item = new MultiWaitItem(input, query, dependencyMsgIds);

        // Register the item under each dependency id so that completing any of those ids can
        // locate and decrement it (see CompleteMultiWaits, wired into the completion path).
        foreach (var id in dependencyMsgIds)
        {
            var bag = _multiWaits.GetOrAdd(id, static _ => new ConcurrentBag<MultiWaitItem>());
            bag.Add(item);
        }

        // Re-check each dependency after registration to close the enqueue/complete race: an id
        // may have completed between the caller's pending check and the registration above. If
        // so, decrement it directly on this item. When the last dependency is cleared this way,
        // execute immediately (exactly once, guarded inside MultiWaitItem).
        foreach (var id in dependencyMsgIds)
        {
            if (ExistsInRecentMessageId(id) && item.RemoveDependency(id))
            {
                ExecuteMultiWaitItem(item);
                break;
            }
        }
    }

    // Completes a single message id for every multi-wait item blocked on it. Removing the id
    // from an item's remaining set and, when that set becomes empty, executing the item's inner
    // query exactly once. Intended to be called from the completion path (task 2.3).
    private void CompleteMultiWaits(long reqMsgId)
    {
        if (!_multiWaits.TryRemove(reqMsgId, out var items))
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.RemoveDependency(reqMsgId))
            {
                ExecuteMultiWaitItem(item);
            }
        }
    }

    // Executes a released multi-wait item's inner query. This runs off the original request
    // thread, so an unresolved inner constructor cannot be surfaced as an RPC error; it is
    // logged and dropped, matching the single-id HandleAsync(long) behavior.
    private void ExecuteMultiWaitItem(MultiWaitItem item)
    {
        if (!handlerHelper.TryGetHandler(item.Query.ConstructorId, out var handler))
        {
            logger.LogError("InvokeAfterMsgs: no handler for inner query {ConstructorId:x8}, dropping",
                item.Query.ConstructorId);
            return;
        }

        _ = ExecuteInnerQueryAsync(handler, item);
    }

    private async Task ExecuteInnerQueryAsync(IObjectHandler handler, MultiWaitItem item)
    {
        try
        {
            await handler.HandleAsync(item.Input, item.Query)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "InvokeAfterMsgs deferred execution failed");
        }
    }

    // Holds one deferred invokeAfterMsgs query and the set of dependency message ids it is still
    // waiting on. The remaining set is guarded by a lock and execution is guarded by an
    // Interlocked flag so the inner query fires exactly once even under concurrent completions.
    private sealed class MultiWaitItem
    {
        private readonly HashSet<long> _remaining;
        private readonly object _lock = new();
        private int _executed;

        public MultiWaitItem(IRequestInput input, IObject query, IEnumerable<long> dependencyMsgIds)
        {
            Input = input;
            Query = query;
            _remaining = [.. dependencyMsgIds];
        }

        public IRequestInput Input { get; }
        public IObject Query { get; }

        // Removes a dependency id. Returns true only for the single caller that both empties the
        // remaining set and wins the execute-once race.
        public bool RemoveDependency(long id)
        {
            lock (_lock)
            {
                _remaining.Remove(id);
                if (_remaining.Count != 0)
                {
                    return false;
                }
            }

            return TryMarkExecuted();
        }

        // Wins the execute-once race exactly once.
        public bool TryMarkExecuted()
        {
            return Interlocked.CompareExchange(ref _executed, 1, 0) == 0;
        }
    }

    public ValueTask AddCompletedReqMsgIdAsync(long reqMsgId)
    {
        return _completedReqMsgIds.Writer.WriteAsync(reqMsgId);
    }

    public async Task ProcessAsync()
    {
        while (await _completedReqMsgIds.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (_completedReqMsgIds.Reader.TryRead(out var reqMsgId))
            {
                try
                {
                    await HandleAsync(reqMsgId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "InvokeAfterMsg failed");
                }
            }
        }
    }

    public Task HandleAsync(long reqMsgId)
    {
        // Service any multi-id (invokeAfterMsgs) deferrals waiting on this id. This must run
        // regardless of whether a single-id (_requests) deferral exists for reqMsgId, so it is
        // invoked before the early return below.
        CompleteMultiWaits(reqMsgId);

        if (_requests.TryGetValue(reqMsgId, out var item))
        {
            if (!handlerHelper.TryGetHandler(item.Query.ConstructorId, out var handler))
            {
                throw new NotImplementedException($"Not supported query: {item.Query.ConstructorId:x2}");
            }

            return handler.HandleAsync(item.Input, item.Query);
        }

        return Task.CompletedTask;
    }

    public Task<IObject> HandleAsync(IRequestInput input,
        IObject query)
    {
        if (!handlerHelper.TryGetHandler(query.ConstructorId, out var handler))
        {
            throw new NotSupportedException($"Not supported query:{query.ConstructorId:x2}");
        }

        return handler.HandleAsync(input, query);
    }
}