namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Returns current configuration, including data center configuration.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getConfig"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✔]
/// </remarks>
internal sealed class GetConfigHandler(
    IOptions<MyTelegramMessengerServerOptions> optionsAccessor,
    IDataCenterHelper dataCenterHelper,
    IUserAppService userAppService,
    ILayeredService<IConfigConverter> layeredService,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Help.RequestGetConfig, MyTelegram.Schema.IConfig>
{
    private readonly MyTelegramMessengerServerOptions _options = optionsAccessor.Value;

    protected override async Task<IConfig> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Help.RequestGetConfig obj)
    {
        var config = layeredService.GetConverter(input.Layer).ToConfig(
            _options.DcOptions.Where(p => p.Enabled).ToList(),
            _options.ThisDcId,
            dataCenterHelper.GetMediaDcId());

        if (input.UserId != 0)
        {
            var key = ((int)UserConfigType.DefaultReaction).ToString();
            var userConfig = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, key));
            if (userConfig?.Value is { Length: > 0 })
            {
                config.ReactionsDefault = userConfig.Value.StartsWith("custom:")
                    && long.TryParse(userConfig.Value[7..], out var docId)
                    ? new TReactionCustomEmoji { DocumentId = docId }
                    : new TReactionEmoji { Emoticon = userConfig.Value };
            }
        }

        return config;
    }
}
