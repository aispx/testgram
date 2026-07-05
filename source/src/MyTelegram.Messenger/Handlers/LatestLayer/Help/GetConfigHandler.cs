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
    private static readonly int[] ClientDcIds = [1, 2, 3, 4, 5];

    protected override async Task<IConfig> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Help.RequestGetConfig obj)
    {
        var config = layeredService.GetConverter(input.Layer).ToConfig(
            BuildAdvertisedDcOptions(_options),
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

    private static List<DcOption> BuildAdvertisedDcOptions(MyTelegramMessengerServerOptions options)
    {
        var enabledOptions = options.DcOptions?
            .Where(p => p.Enabled)
            .ToList() ?? [];

        if (enabledOptions.Count == 0)
        {
            return [];
        }

        var advertisedOptions = new List<DcOption>(enabledOptions);

        AddMissingDcOptions(advertisedOptions, enabledOptions, options.ThisDcId, mediaOnly: false);
        AddMissingDcOptions(advertisedOptions, enabledOptions, MyTelegramConsts.MediaDcId, mediaOnly: true);

        return advertisedOptions;
    }

    private static void AddMissingDcOptions(
        List<DcOption> advertisedOptions,
        List<DcOption> enabledOptions,
        int preferredTemplateDcId,
        bool mediaOnly)
    {
        var template = enabledOptions.FirstOrDefault(p => p.Id == preferredTemplateDcId && p.MediaOnly == mediaOnly)
                       ?? enabledOptions.FirstOrDefault(p => p.MediaOnly == mediaOnly)
                       ?? enabledOptions.First();

        foreach (var dcId in ClientDcIds)
        {
            if (advertisedOptions.Any(p => p.Id == dcId && p.MediaOnly == mediaOnly))
            {
                continue;
            }

            advertisedOptions.Add(CloneDcOption(template, dcId, mediaOnly));
        }
    }

    private static DcOption CloneDcOption(DcOption source, int dcId, bool mediaOnly)
    {
        return new DcOption
        {
            Enabled = true,
            Ipv6 = source.Ipv6,
            MediaOnly = mediaOnly,
            TcpoOnly = source.TcpoOnly,
            ThisPortOnly = source.ThisPortOnly,
            Cdn = source.Cdn,
            Static = source.Static,
            Id = dcId,
            Port = source.Port,
            Secret = source.Secret?.ToArray(),
            IpAddress = source.IpAddress
        };
    }
}
