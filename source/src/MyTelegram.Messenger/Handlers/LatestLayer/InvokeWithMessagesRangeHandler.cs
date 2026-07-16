namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Invoke with the given message range
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithMessagesRange"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithMessagesRangeHandler : BaseObjectHandler<MyTelegram.Schema.RequestInvokeWithMessagesRange, IObject>
{
    private readonly IHandlerHelper _handlerHelper;
    public InvokeWithMessagesRangeHandler(IHandlerHelper handlerHelper)
    {
        _handlerHelper = handlerHelper;
    }

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInvokeWithMessagesRange obj)
    {
        // Transparent pass-through: execute the wrapped inner query and return its result.
        // The `Range` field is accepted but intentionally ignored — this server does not gate
        // execution on message ranges (range-based statistics/flood gating is out of scope), and
        // ignoring it must not cause an error.
        return await SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
    }
}
