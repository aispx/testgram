using MyTelegram.Converters.Services.Interfaces;
using MyTelegram.Schema.Auth;
using MyTelegram.Schema.Help;
using IAuthorization = MyTelegram.Schema.Auth.IAuthorization;
using TAuthorization = MyTelegram.Schema.Auth.TAuthorization;

namespace MyTelegram.Converters.TLObjects.LatestLayer;

internal sealed class AuthorizationConverter(IObjectMapper objectMapper, ISessionLocationResolver sessionLocationResolver) : IAuthorizationConverter, ITransientDependency
{
    
    public int Layer => Layers.LayerLatest;

    public IAuthorization CreateAuthorization(IUser? user, bool setupPasswordRequired = false)
    {
        if (user == null)
        {
            return new TAuthorizationSignUpRequired
            {
                TermsOfService = new TTermsOfService
                {
                    Entities = new TVector<IMessageEntity>(),
                    Id = new TDataJSON
                    {
                        Data =
                            "{\"country\":\"US\",\"min_age\":false,\"terms_key\":\"TERMS_OF_SERVICE\",\"terms_lang\":\"en\",\"terms_version\":1,\"terms_hash\":\"7dca806cb8d387c07c778ce9ef6aac04\"}"
                    },
                    Text =
                        "By signing up for MyTelegram, you agree not to:\n\n- Use our service to send spam or scam users.\n- Promote violence on publicly viewable Telegram bots, groups or channels.\n- Post pornographic content on publicly viewable MyTelegram bots, groups or channels.\n\nWe reserve the right to update these Terms of Service later."
                }
            };
        }

        return new TAuthorization
        {
            User = user,
            SetupPasswordRequired = setupPasswordRequired,
            OtherwiseReloginDays = setupPasswordRequired ? 1 : null
        };
    }

    public IAuthorization CreateSignUpAuthorization()
    {
        return new TAuthorizationSignUpRequired
        {
            TermsOfService = new TTermsOfService
            {
                Entities = new TVector<IMessageEntity>(),
                Id = new TDataJSON
                {
                    Data =
                        "{\"country\":\"US\",\"min_age\":false,\"terms_key\":\"TERMS_OF_SERVICE\",\"terms_lang\":\"en\",\"terms_version\":1,\"terms_hash\":\"7dca806cb8d387c07c778ce9ef6aac04\"}"
                },
                Text =
                    "By signing up for MyTelegram, you agree not to:\n\n- Use our service to send spam or scam users.\n- Promote violence on publicly viewable Telegram bots, groups or channels.\n- Post pornographic content on publicly viewable MyTelegram bots, groups or channels.\n\nWe reserve the right to update these Terms of Service later."
            }
        };
    }

    public Schema.IAuthorization ToAuthorization(IDeviceReadModel deviceReadModel, long selfPermAuthKeyId = -1)
    {
        var authorization = objectMapper.Map<IDeviceReadModel, Schema.TAuthorization>(deviceReadModel);
        if (string.IsNullOrWhiteSpace(authorization.Platform))
        {
            authorization.Platform = !string.IsNullOrWhiteSpace(deviceReadModel.LangPack)
                ? deviceReadModel.LangPack
                : deviceReadModel.ApiId == 4
                    ? "android"
                    : authorization.Platform;
        }
        var location = sessionLocationResolver.Resolve(deviceReadModel);
        authorization.Country = location.Country;
        authorization.Region = location.Region;

        authorization.Current = selfPermAuthKeyId == deviceReadModel.PermAuthKeyId;

        return authorization;
    }

    public IWebAuthorization ToWebAuthorization(IDeviceReadModel deviceReadModel, long selfPermAuthKeyId = -1)
    {
        var authorization = objectMapper.Map<IDeviceReadModel, TWebAuthorization>(deviceReadModel);
        var location = sessionLocationResolver.Resolve(deviceReadModel);
        authorization.Region = location.Region;
        authorization.Domain = deviceReadModel.Parameters?.TryGetValue("domain", out var domain) == true ? domain : string.Empty;

        return authorization;
    }

    public IReadOnlyList<Schema.IAuthorization> ToAuthorizations(IReadOnlyCollection<IDeviceReadModel> deviceReadModels,
        long selfPermAuthKeyId = -1)
    {
        return deviceReadModels.Select(p => ToAuthorization(p, selfPermAuthKeyId)).ToList();
    }

    public IReadOnlyList<IWebAuthorization> ToWebAuthorizations(IReadOnlyCollection<IDeviceReadModel> deviceReadModels,
        long selfPermAuthKeyId = -1)
    {
        return deviceReadModels.Select(p => ToWebAuthorization(p, selfPermAuthKeyId)).ToList();
    }
}
