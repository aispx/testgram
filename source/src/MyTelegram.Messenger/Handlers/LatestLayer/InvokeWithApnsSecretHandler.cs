namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Official clients only, invoke with Apple push verification.
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithApnsSecret"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithApnsSecretHandler : RpcResultObjectHandler<MyTelegram.Schema.RequestInvokeWithApnsSecret, IObject>
{
    private readonly IHandlerHelper _handlerHelper;
    public InvokeWithApnsSecretHandler(IHandlerHelper handlerHelper)
    {
        _handlerHelper = handlerHelper;
    }

    protected override Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInvokeWithApnsSecret obj)
    {
        // Transparent pass-through: this self-hosted server cannot perform Apple APNs
        // device attestation, so the attestation fields (nonce/secret) are accepted and
        // ignored, and the wrapped inner query is executed and its result returned.
        return SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
    }
}
