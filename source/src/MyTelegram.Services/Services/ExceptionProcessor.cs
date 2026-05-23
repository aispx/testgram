using EventFlow.Exceptions;

namespace MyTelegram.Services.Services;

public class ExceptionProcessor(
    ILogger<ExceptionProcessor> logger,
    IObjectMessageSender objectMessageSender,
    IEventBus eventBus)
    : IExceptionProcessor, ITransientDependency
{
    public Task HandleExceptionAsync(Exception ex, IRequestInput input, IObject? requestData, string? handlerName)
    {
        LogException(ex, input, requestData, handlerName);
        return ProcessExceptionCoreAsync(ex, input.UserId, input);
    }

    private void LogException(Exception ex, IRequestInput input, IObject? requestData, string? handlerName)
    {
        if (ex is RpcException { RpcError.ErrorCode: < MyTelegramConsts.InternalErrorCode } rpcException)
        {
            logger.LogWarning(
                "Process request returned RPC error, handler: {HandlerName}, userId: {UserId}, errorCode: {ErrorCode}, errorMessage: {ErrorMessage}, requestInput: {@RequestInput}, requestData: {@RequestData}",
                handlerName,
                input.UserId,
                rpcException.RpcError.ErrorCode,
                rpcException.RpcError.Message,
                input,
                requestData
            );
            return;
        }

        logger.LogError(ex,
            "Process request failed, handler: {HandlerName}, userId: {UserId}, requestInput: {@RequestInput}, requestData: {@RequestData}",
            handlerName,
            input.UserId,
            input,
            requestData
        );
    }

    private async Task ProcessExceptionCoreAsync(Exception ex, long userId, IRequestInput input)
    {
        string errorMessage;
        int errorCode;
        switch (ex)
        {
            case DuplicateOperationException:
                var eventData = new DuplicateCommandEvent(input.PermAuthKeyId, userId, input.ReqMsgId);
                await eventBus.PublishAsync(eventData);
                return;
            case NotImplementedException:
                errorCode = MyTelegramConsts.InternalErrorCode;
                errorMessage = "API NotImplemented";
                break;

            case RpcException rpcException:
                errorCode = rpcException.RpcError.ErrorCode;
                errorMessage = rpcException.RpcError.Message;
                break;

            case DomainError domainError:
                errorCode = MyTelegramConsts.InternalErrorCode;
                errorMessage = domainError.Message;
                break;

            case SagaPublishException sagaPublishException:
                var innerException = sagaPublishException.InnerException;
                errorMessage = innerException switch
                {
                    CommandException { InnerException: RpcException subInnerException } => subInnerException
                        .Message,
                    _ => MyTelegramConsts.InternalErrorMessage
                };
                errorCode = MyTelegramConsts.BadRequestErrorCode;
                break;

            default:
                errorCode = MyTelegramConsts.InternalErrorCode;
                errorMessage = MyTelegramConsts.InternalErrorMessage;
                break;
        }

        var rpcError = new TRpcError { ErrorCode = errorCode, ErrorMessage = errorMessage };
        var rpcResult = new TRpcResult { ReqMsgId = input.ReqMsgId, Result = rpcError };

        await objectMessageSender.SendMessageToPeerAsync(input.ToRequestInfo(), rpcResult);
    }
}