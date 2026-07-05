namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Login by importing an authorization token
/// Possible errors
/// Code Type Description
/// 400 API_ID_INVALID API ID invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.importWebTokenAuthorization"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class ImportWebTokenAuthorizationHandler(
    IWebTokenAuthCacheHelper webTokenAuthCacheHelper,
    IEventBus eventBus,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestImportWebTokenAuthorization, MyTelegram.Schema.Auth.IAuthorization>
{
    protected override async Task<MyTelegram.Schema.Auth.IAuthorization> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestImportWebTokenAuthorization obj)
    {
        // 1. Validate the api id. There is no server-side registry of api ids in this codebase,
        //    so a positive api id is treated as valid; a non-positive one is rejected
        //    (Requirement 6.2).
        if (obj.ApiId <= 0)
        {
            RpcErrors.RpcErrors400.ApiIdInvalid.ThrowRpcError();
            return null!;
        }

        // 2. Resolve the web auth token to the authorized account (Requirement 6.3).
        if (!webTokenAuthCacheHelper.TryGetValue(obj.WebAuthToken, out var webTokenItem) ||
            webTokenItem == null)
        {
            RpcErrors.RpcErrors400.AuthTokenInvalid.ThrowRpcError();
            return null!;
        }

        // 3. Bind the resolved user to the current session and build the authorization
        //    (Requirement 6.1), mirroring ImportLoginTokenHandler.
        var userId = webTokenItem.UserId;
        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));
        var userReadModel = await userAppService.GetAsync(userId);
        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        ILayeredUser? user = userReadModel == null ? null : userConverterService.ToUser(input, userReadModel, photos);
        return layeredService.GetConverter(input.Layer).CreateAuthorization(user);
    }
}
