namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Official clients only: re-execute a method call that required reCAPTCHA verification via a <code>RECAPTCHA_CHECK_%s__%s</code>, where the first placeholder is the <code>action</code>, and the second one is the reCAPTCHA key ID.
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithReCaptcha"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithReCaptchaHandler : RpcResultObjectHandler<MyTelegram.Schema.RequestInvokeWithReCaptcha, IObject>
{
    private readonly IHandlerHelper _handlerHelper;
    public InvokeWithReCaptchaHandler(IHandlerHelper handlerHelper)
    {
        _handlerHelper = handlerHelper;
    }

    protected override Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInvokeWithReCaptcha obj)
    {
        // Transparent pass-through: this server cannot verify the reCAPTCHA attestation, so the
        // attestation fields (action/token) are accepted but ignored, and the inner query is
        // executed directly. An unresolved inner constructor yields 400 INPUT_CONSTRUCTOR_INVALID.
        return SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, input, obj.Query);
    }
}
