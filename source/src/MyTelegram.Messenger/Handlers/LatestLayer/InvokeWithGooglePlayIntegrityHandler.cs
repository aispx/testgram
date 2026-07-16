namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Official clients only, invoke with Google Play Integrity token.
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithGooglePlayIntegrity"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithGooglePlayIntegrityHandler : RpcResultObjectHandler<MyTelegram.Schema.RequestInvokeWithGooglePlayIntegrity, IObject>
{
    private readonly IHandlerHelper _handlerHelper;

    public InvokeWithGooglePlayIntegrityHandler(IHandlerHelper handlerHelper)
    {
        _handlerHelper = handlerHelper;
    }

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInvokeWithGooglePlayIntegrity obj)
    {
        // Transparent pass-through: the attestation fields (obj.Nonce / obj.Token) are
        // Google Play Integrity payloads that a self-hosted server cannot verify, so they
        // are accepted and ignored. The inner query is dispatched and executed as-is.
        return await SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
    }
}