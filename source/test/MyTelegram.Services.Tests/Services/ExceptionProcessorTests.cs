using Microsoft.Extensions.Logging;
using Moq;
using MyTelegram.Abstractions;
using MyTelegram.EventBus;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Services;

public class ExceptionProcessorTests
{
    [Fact]
    public async Task HandleExceptionAsync_ShouldLogClientRpcErrorsAsWarning_AndSendRpcError()
    {
        var logger = new TestLogger<ExceptionProcessor>();
        var objectMessageSender = new Mock<IObjectMessageSender>();
        var eventBus = new Mock<IEventBus>();
        TRpcResult? sentResult = null;

        objectMessageSender
            .Setup(s => s.SendMessageToPeerAsync(It.IsAny<RequestInfo>(), It.IsAny<TRpcResult>()))
            .Callback<RequestInfo, TRpcResult>((_, result) => sentResult = result)
            .Returns(Task.CompletedTask);

        var input = new RequestInput(
            "connection",
            ConnectionType.Generic,
            Guid.NewGuid(),
            0x2efe1722,
            7642812086519253612,
            309,
            2091001,
            -5765431725600661026,
            -8536330271488447713,
            224,
            1779480857859,
            DeviceType.Desktop,
            "2.26.13.4",
            9113609876595645565,
            8709748734689880470);

        var sut = new ExceptionProcessor(logger, objectMessageSender.Object, eventBus.Object);

        await sut.HandleExceptionAsync(
            new RpcException(RpcErrors.RpcErrors400.CallAlreadyDeclined),
            input,
            requestData: null,
            handlerName: "ConfirmCallHandler");

        logger.Entries.ShouldContain(e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("CALL_ALREADY_DECLINED", StringComparison.Ordinal));
        logger.Entries.ShouldNotContain(e => e.LogLevel == LogLevel.Error);

        sentResult.ShouldNotBeNull();
        sentResult.ReqMsgId.ShouldBe(input.ReqMsgId);
        var rpcError = sentResult.Result.ShouldBeOfType<TRpcError>();
        rpcError.ErrorCode.ShouldBe(RpcErrors.RpcErrors400.CallAlreadyDeclined.ErrorCode);
        rpcError.ErrorMessage.ShouldBe(RpcErrors.RpcErrors400.CallAlreadyDeclined.Message);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
