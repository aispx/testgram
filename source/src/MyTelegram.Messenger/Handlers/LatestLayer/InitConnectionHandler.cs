namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Initialize connection
/// Possible errors
/// Code Type Description
/// 400 CONNECTION_LAYER_INVALID Layer invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/initConnection"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✔]
/// </remarks>
internal sealed class InitConnectionHandler : BaseObjectHandler<MyTelegram.Schema.RequestInitConnection, IObject>
{
    private readonly IHandlerHelper _handlerHelper;
    public InitConnectionHandler(IHandlerHelper handlerHelper)
    {
        _handlerHelper = handlerHelper;
    }

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInitConnection obj)
    {
        // initConnection is normally unwrapped at the session decode layer (layer/connection
        // parameters are lifted there), so this handler is not reached on the normal path.
        // If any path ever dispatches it, act as a transparent pass-through: execute the inner
        // query when present, otherwise there is nothing to run.
        if (obj.Query is null)
        {
            return null!;
        }

        return await SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
    }
}
