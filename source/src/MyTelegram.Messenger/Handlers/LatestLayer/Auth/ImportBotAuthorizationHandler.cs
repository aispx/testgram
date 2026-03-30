using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class ImportBotAuthorizationHandler(
    IEventBus eventBus,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestImportBotAuthorization, MyTelegram.Schema.Auth.IAuthorization>
{
    private const string BotCollection = "xiefather-bot-state";

    protected override async Task<MyTelegram.Schema.Auth.IAuthorization> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestImportBotAuthorization obj)
    {
        var bot = await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(new BsonDocument("Token", obj.BotAuthToken))
            .FirstOrDefaultAsync();

        if (bot == null)
            RpcErrors.RpcErrors400.AccessTokenInvalid.ThrowRpcError();

        var botUserId = bot!["BotUserId"].AsInt64;
        var userReadModel = await userAppService.GetAsync(botUserId);
        if (userReadModel == null)
            RpcErrors.RpcErrors400.AccessTokenInvalid.ThrowRpcError();

        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userReadModel!.UserId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));

        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        ILayeredUser user = userConverterService.ToUser(input, userReadModel, photos, layer: input.Layer);
        user.Self = true;
        return layeredService.GetConverter(input.Layer).CreateAuthorization(user);
    }
}
