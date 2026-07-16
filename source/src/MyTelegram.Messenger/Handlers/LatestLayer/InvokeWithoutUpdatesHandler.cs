using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Invoke a request without subscribing the used connection for <a href="https://corefork.telegram.org/api/updates">updates</a> (this is enabled by default for <a href="https://corefork.telegram.org/api/files">file queries</a>).
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithoutUpdates"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithoutUpdatesHandler : BaseObjectHandler<MyTelegram.Schema.RequestInvokeWithoutUpdates, IObject>
{
    private readonly IHandlerHelper _handlerHelper;
    public InvokeWithoutUpdatesHandler(IHandlerHelper handlerHelper)
    {
        _handlerHelper = handlerHelper;
    }

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, RequestInvokeWithoutUpdates obj)
    {
        // Suppress update delivery for the current connection while the inner query runs.
        // The scope must remain active for the full duration of execution, so we await
        // inside the using block and let the AsyncLocal flag reset afterward.
        using (NoUpdatesContext.Enter())
        {
            return await SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
        }
    }
}
