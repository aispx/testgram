using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram;
using MyTelegram.Abstractions;
using MyTelegram.Messenger;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests;

/// <summary>
/// Example-based tests for <c>InvokeAfterMsgsHandler</c> (the <c>invokeAfterMsgs</c> wrapper).
///
/// <para>The handler is <c>internal</c> in the <c>MyTelegram.Messenger</c> assembly, so it is
/// instantiated via reflection and invoked through the public <see cref="IObjectHandler"/>
/// entrypoint (which routes to the protected <c>HandleCoreAsync</c>). The real
/// <see cref="InvokeAfterMsgProcessor"/> is used to exercise the pending -> deferred -> execute
/// flow, with a fake <see cref="IHandlerHelper"/> resolving the inner query's constructor id to
/// a recording handler.</para>
///
/// <b>Validates: Requirements 1.1, 1.2, 1.3, 1.4</b>
/// </summary>
public class InvokeAfterMsgsHandlerTests
{
    // Requirement 1.1: empty MsgIds -> inner query executed immediately, returning its result.
    [Fact]
    public void EmptyMsgIds_ExecutesInnerQueryImmediately()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var processor = NewProcessor(handlerHelper);
        var request = new RequestInvokeAfterMsgs { MsgIds = new TVector<long>(), Query = query };

        var result = Invoke(processor, handlerHelper, new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        result.ShouldBe(handler.Result);
    }

    // Requirement 1.2: all ids already completed -> inner query executed immediately.
    [Fact]
    public void AllIdsAlreadyCompleted_ExecutesInnerQueryImmediately()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var processor = NewProcessor(handlerHelper);

        long[] ids = [10L, 20L, 30L];
        foreach (var id in ids)
        {
            processor.AddToRecentMessageIdList(id);
        }

        var request = new RequestInvokeAfterMsgs { MsgIds = new TVector<long>(ids), Query = query };

        var result = Invoke(processor, handlerHelper, new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        result.ShouldBe(handler.Result);
    }

    // Requirement 1.3: some ids pending -> deferred (returns null, not yet executed); the inner
    // query executes exactly once when the last pending id completes.
    [Fact]
    public async Task SomePending_IsDeferred_ThenExecutesOnceOnLastCompletion()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var processor = NewProcessor(handlerHelper);

        long[] ids = [101L, 102L, 103L];
        // None added to the recent list, so all three are pending.
        foreach (var id in ids)
        {
            processor.ExistsInRecentMessageId(id).ShouldBeFalse();
        }

        var request = new RequestInvokeAfterMsgs { MsgIds = new TVector<long>(ids), Query = query };

        var result = Invoke(processor, handlerHelper, new FakeRequestInput(), request);

        // Deferred: no immediate response and the inner query has not run.
        result.ShouldBeNull();
        handler.CallCount.ShouldBe(0);

        // Complete the first two dependencies: still not all done, must not execute.
        await processor.HandleAsync(ids[0]);
        handler.CallCount.ShouldBe(0);
        await processor.HandleAsync(ids[1]);
        handler.CallCount.ShouldBe(0);

        // Complete the last dependency: inner query executes exactly once.
        await processor.HandleAsync(ids[2]);
        WaitForCount(handler, expected: 1);
        handler.CallCount.ShouldBe(1);
    }

    // Requirement 1.4: unknown inner constructor on the immediate path -> INPUT_CONSTRUCTOR_INVALID
    // (a 400 RPC error), not a NotImplementedException.
    [Fact]
    public void UnknownInnerConstructor_ImmediatePath_ThrowsInputConstructorInvalid()
    {
        var query = new FakeQuery();
        // Handler helper resolves a DIFFERENT constructor id, so the inner query is unresolved.
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId + 1, new RecordingHandler());
        var processor = NewProcessor(handlerHelper);

        // Empty MsgIds forces the immediate (request-thread) execution path.
        var request = new RequestInvokeAfterMsgs { MsgIds = new TVector<long>(), Query = query };

        var ex = Should.Throw<RpcException>(() =>
            Invoke(processor, handlerHelper, new FakeRequestInput(), request));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("INPUT_CONSTRUCTOR_INVALID");
    }

    private static InvokeAfterMsgProcessor NewProcessor(IHandlerHelper handlerHelper) =>
        new(handlerHelper, NullLogger<InvokeAfterMsgProcessor>.Instance);

    // Instantiates the internal InvokeAfterMsgsHandler via reflection and invokes it through the
    // public IObjectHandler entrypoint.
    private static IObject? Invoke(
        IInvokeAfterMsgProcessor processor,
        IHandlerHelper handlerHelper,
        IRequestInput input,
        RequestInvokeAfterMsgs request)
    {
        var handlerType = typeof(MyTelegramMessengerServerOptions).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.InvokeAfterMsgsHandler",
            throwOnError: true)!;

        var handler = (IObjectHandler)Activator.CreateInstance(
            handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [processor, handlerHelper],
            culture: null)!;

        try
        {
            return handler.HandleAsync(input, request).GetAwaiter().GetResult();
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static void WaitForCount(RecordingHandler handler, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (handler.CallCount < expected && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1);
        }
    }

    // Records inner-query invocations (thread-safe for the deferred completion path) and returns
    // a known result so the immediate-path tests can assert the wrapper returns the inner result.
    private sealed class RecordingHandler : IObjectHandler
    {
        private int _callCount;

        public IObject Result { get; } = new FakeQuery();

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IObject> HandleAsync(IRequestInput request, IObject obj)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(Result);
        }
    }

    // Fake IHandlerHelper resolving exactly one constructor id to a recording handler; any other
    // id is unresolved (returns false), exercising the INPUT_CONSTRUCTOR_INVALID path.
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

    // A minimal IRequestInput carried through the wrapper; its field values are irrelevant here.
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
