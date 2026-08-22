using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Returns a Telegram Passport authorization form for sharing data with a service
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 PUBLIC_KEY_REQUIRED A public key is required.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getAuthorizationForm"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAuthorizationFormHandler(
    IPassportBotResolver passportBotResolver,
    IPassportValueStore passportValueStore,
    IPassportErrorStore passportErrorStore,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetAuthorizationForm,
        MyTelegram.Schema.Account.IAuthorizationForm>
{
    protected override async Task<MyTelegram.Schema.Account.IAuthorizationForm> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetAuthorizationForm obj)
    {
        var bot = await passportBotResolver.ResolveAsync(obj.BotId, obj.PublicKey);

        var requiredTypes = PassportScopeParser.Parse(obj.Scope);

        // "If the form was already submitted at least once, the constructor will also contain a list of
        // already submitted data, along with eventual errors." Only the types this scope asks for are
        // returned - the service has no business seeing what else the user stored.
        var requestedTypes = CollectTypes(requiredTypes);
        var documents = await passportValueStore.GetAsync(input.UserId, requestedTypes);

        return new MyTelegram.Schema.Account.TAuthorizationForm
        {
            RequiredTypes = requiredTypes,
            Values = await passportValueStore.ToSecureValuesAsync(input.UserId, documents),
            Errors = await passportErrorStore.GetAsync(input.UserId, obj.BotId),
            Users = new TVector<IUser>(userConverterService.ToUser(input, bot.ReadModel, layer: input.Layer)),
            PrivacyPolicyUrl = bot.PrivacyPolicyUrl
        };
    }

    private static List<uint> CollectTypes(TVector<ISecureRequiredType> requiredTypes)
    {
        var result = new List<uint>();
        Collect(requiredTypes, result);

        return result;
    }

    private static void Collect(IEnumerable<ISecureRequiredType> types, List<uint> destination)
    {
        foreach (var type in types)
        {
            switch (type)
            {
                case TSecureRequiredType required when !destination.Contains(required.Type.ConstructorId):
                    destination.Add(required.Type.ConstructorId);
                    break;
                case TSecureRequiredTypeOneOf oneOf:
                    Collect(oneOf.Types, destination);
                    break;
            }
        }
    }
}
