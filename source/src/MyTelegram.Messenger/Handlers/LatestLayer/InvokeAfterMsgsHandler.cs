namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Invokes a query after a successful completion of previous queries
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeAfterMsgs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeAfterMsgsHandler : BaseObjectHandler<MyTelegram.Schema.RequestInvokeAfterMsgs, IObject>
{
    private readonly IInvokeAfterMsgProcessor _invokeAfterMsgProcessor;
    private readonly IHandlerHelper _handlerHelper;
    public InvokeAfterMsgsHandler(IInvokeAfterMsgProcessor invokeAfterMsgProcessor, IHandlerHelper handlerHelper)
    {
        _invokeAfterMsgProcessor = invokeAfterMsgProcessor;
        _handlerHelper = handlerHelper;
    }

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, RequestInvokeAfterMsgs obj)
    {
        // Run the inner query only after every referenced message id has completed. Any ids
        // already tracked as completed by the processor are satisfied; the rest are pending.
        var pending = obj.MsgIds.Where(id => !_invokeAfterMsgProcessor.ExistsInRecentMessageId(id)).ToList();
        if (pending.Count == 0)
        {
            // All dependencies (or none) already completed: execute the inner query now.
            return await SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
        }

        // Some dependencies are still outstanding: defer via the shared processor.
        _invokeAfterMsgProcessor.EnqueueAfterMsgs(pending, input, obj.Query);
        return null !;
    }
}
