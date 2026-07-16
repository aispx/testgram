namespace MyTelegram.Messenger.Handlers;

/// <summary>
/// Shared helper for "invoking" wrappers that must dispatch and execute their inner
/// <see cref="IObject"/> query. Centralizes inner-query resolution and the standard
/// <c>400 INPUT_CONSTRUCTOR_INVALID</c> error emitted when no handler is registered for
/// the inner constructor, so every wrapper surfaces a consistent RPC error instead of
/// throwing <see cref="NotImplementedException"/>.
/// </summary>
internal static class SubQueryExecutor
{
    public static async Task<IObject> ExecuteInnerAsync(
        IHandlerHelper handlerHelper, IRequestInput input, IObject query)
    {
        if (handlerHelper.TryGetHandler(query.ConstructorId, out var handler))
        {
            return await handler.HandleAsync(input, query)!;
        }

        RpcErrors.RpcErrors400.InputConstructorInvalid.ThrowRpcError();
        return null!;
    }
}
