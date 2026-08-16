using System.Diagnostics.CodeAnalysis;
using System.Threading;
using CsCheck;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram;
using MyTelegram.Abstractions;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using MyTelegram.Services.Tests.Phone;

namespace MyTelegram.Services.Tests;

/// <summary>
/// Property-based tests for the multi-dependency deferral in
/// <see cref="InvokeAfterMsgProcessor"/> (the <c>invokeAfterMsgs</c> mechanism).
///
/// <para>Exercises the public surface used by the <c>invokeAfterMsgs</c> handler:
/// <see cref="InvokeAfterMsgProcessor.EnqueueAfterMsgs"/> to register a multi-wait item whose
/// dependency ids are all still pending, and <see cref="InvokeAfterMsgProcessor.HandleAsync(long)"/>
/// to signal completion of each dependency id. The inner query is a recording handler that counts
/// how many times it is invoked.</para>
///
/// <b>Validates: Requirements 1.3, 1.5</b>
/// </summary>
public class InvokeAfterMsgProcessorMultiWaitPropertyTests
{
    // Feature: invoking-wrappers-completion, Property 1: A multi-wait item executes exactly once, only after all its dependency ids complete (in any order)
    [Fact]
    public void MultiWaitItem_ExecutesExactlyOnce_OnlyAfterAllDependenciesComplete()
    {
        // Generate a set of distinct dependency ids (size 1..8) plus a random permutation of the
        // order in which those ids complete. The permutation is produced by attaching a random
        // sort key to each id and ordering by it, so completion order is independent of id order.
        var gen =
            from n in Gen.Int[1, 8]
            from ids in Gen.Long[1, 1_000_000].Array[n].Where(a => a.Distinct().Count() == a.Length)
            from keys in Gen.Int.Array[n]
            select (
                ids,
                completionOrder: ids
                    .Zip(keys, (id, key) => (id, key))
                    .OrderBy(t => t.key)
                    .Select(t => t.id)
                    .ToArray());

        gen.Sample(scenario => RunScenario(scenario.ids, scenario.completionOrder), iter: 100);
    }

    private static void RunScenario(long[] dependencyIds, long[] completionOrder)
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var processor = new InvokeAfterMsgProcessor(
            handlerHelper,
            new CapturingObjectMessageSender(),
            NullLogger<InvokeAfterMsgProcessor>.Instance);

        var input = new FakeRequestInput();

        // All dependency ids are pending: none has been added to the recent-message list, so the
        // item is genuinely deferred rather than executed immediately on enqueue.
        foreach (var id in dependencyIds)
        {
            processor.ExistsInRecentMessageId(id).ShouldBeFalse();
        }

        processor.EnqueueAfterMsgs(dependencyIds, input, query);

        // Nothing should have executed yet: not all dependencies are complete.
        handler.CallCount.ShouldBe(0, "inner query ran before any dependency completed");

        for (var i = 0; i < completionOrder.Length; i++)
        {
            processor.HandleAsync(completionOrder[i]).GetAwaiter().GetResult();

            var isLast = i == completionOrder.Length - 1;
            if (!isLast)
            {
                // Completing a strict subset must NOT trigger execution.
                handler.CallCount.ShouldBe(0,
                    $"inner query ran after completing {i + 1}/{completionOrder.Length} dependencies");
            }
            else
            {
                // The inner query executes on a background task off the completion path; poll the
                // thread-safe counter with a short timeout to observe the (normally synchronous)
                // invocation deterministically.
                WaitForCount(handler, expected: 1);
                handler.CallCount.ShouldBe(1, "inner query did not run exactly once after all dependencies completed");
            }
        }

        // Completing already-completed ids again must not re-trigger execution.
        foreach (var id in completionOrder)
        {
            processor.HandleAsync(id).GetAwaiter().GetResult();
        }

        WaitForCount(handler, expected: 1);
        handler.CallCount.ShouldBe(1, "inner query ran more than once");
    }

    private static void WaitForCount(RecordingHandler handler, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (handler.CallCount < expected && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1);
        }
    }

    // Records how many times the inner query handler is invoked, thread-safe for the background
    // completion execution path.
    private sealed class RecordingHandler : IObjectHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IObject> HandleAsync(IRequestInput request, IObject obj)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult<IObject>(new FakeQuery());
        }
    }

    // A fake IHandlerHelper that resolves exactly one constructor id to a recording handler.
    private sealed class FakeHandlerHelper(uint queryConstructorId, IObjectHandler resolvedHandler) : IHandlerHelper
    {
        public void InitAllHandlers() { }

        public bool TryGetHandler(uint objectId, [NotNullWhen(true)] out IObjectHandler? handler)
        {
            if (objectId == queryConstructorId)
            {
                handler = resolvedHandler;
                return true;
            }

            handler = null;
            return false;
        }

        public bool TryGetHandlerName(uint objectId, [NotNullWhen(true)] out string? handlerName)
        {
            handlerName = null;
            return false;
        }

        public bool TryGetHandlerShortName(uint objectId, [NotNullWhen(true)] out string? handlerShortName)
        {
            handlerShortName = null;
            return false;
        }

        public string GetHandlerFullName(IObject requestData) => string.Empty;
    }

    // A fake inner-query object with a stable constructor id.
    private sealed class FakeQuery : IObject
    {
        public uint ConstructorId => 0x1234_5678u;

        public void Serialize(IBufferWriter<byte> writer) { }

        public void Deserialize(ref ReadOnlyMemory<byte> buffer) { }
    }

    // A minimal IRequestInput carried through the deferral; its field values are irrelevant to the
    // multi-wait counting property.
    private sealed class FakeRequestInput : IRequestInput
    {
        public string ConnectionId => string.Empty;
        public ConnectionType ConnectionType => default;
        public long AuthKeyId => 0;
        public uint ObjectId { get; set; }
        public long PermAuthKeyId => 0;
        public long ReqMsgId => 0;
        public int SeqNumber => 0;
        public Guid RequestId => Guid.Empty;
        public long Date => 0;
        public DeviceType DeviceType { get; set; }
        public string ClientIp => string.Empty;
        public long SessionId => 0;
        public long UserId => 0;
        public long AccessHashKeyId => 0;
        public int Layer { get; set; }
    }
}
