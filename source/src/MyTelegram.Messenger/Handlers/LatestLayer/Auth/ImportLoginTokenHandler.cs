namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Login using a redirected login token, generated in case of DC mismatch during <a href="https://corefork.telegram.org/api/qr-login">QR code login</a>.For more info, see <a href="https://corefork.telegram.org/api/qr-login">login via QR code</a>.
/// Possible errors
/// Code Type Description
/// 400 AUTH_TOKEN_ALREADY_ACCEPTED The specified auth token was already accepted.
/// 400 AUTH_TOKEN_EXPIRED The authorization token has expired.
/// 400 AUTH_TOKEN_INVALID The specified auth token is invalid.
/// 400 AUTH_TOKEN_INVALIDX The specified auth token is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.importLoginToken"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class ImportLoginTokenHandler(ICacheHelper<long, CacheLoginToken> authKeyCacheHelper,
    ICacheHelper<string, CacheLoginToken> tokenCacheHelper,
    IEventBus eventBus,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestImportLoginToken, MyTelegram.Schema.Auth.ILoginToken>
{
    protected override async Task<MyTelegram.Schema.Auth.ILoginToken> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestImportLoginToken obj)
    {
        var tokenKey = CacheLoginToken.GetTokenKey(obj.Token);
        if (!tokenCacheHelper.TryGetValue(tokenKey, out var loginToken) ||
            loginToken == null ||
            !loginToken.Token.AsSpan().SequenceEqual(obj.Token.Span))
        {
            RpcErrors.RpcErrors400.AuthTokenInvalid.ThrowRpcError();
            return null!;
        }

        if (!tokenCacheHelper.TryRemove(tokenKey, out loginToken) || loginToken == null)
        {
            RpcErrors.RpcErrors400.AuthTokenAlreadyAccepted.ThrowRpcError();
            return null!;
        }

        authKeyCacheHelper.TryRemove(loginToken.AuthKeyId, out _);
        var userId = loginToken.UserId;
        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));
        var userReadModel = await userAppService.GetAsync(userId);
        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        ILayeredUser? user = userReadModel == null ? null : userConverterService.ToUser(input, userReadModel, photos);
        return new TLoginTokenSuccess
        {
            Authorization = layeredService.GetConverter(input.Layer).CreateAuthorization(user)
        };
    }
}
