using MyTelegram.EventBus.RabbitMQ.Extensions;
using MyTelegramConsts = MyTelegram.MyTelegramConsts;

Console.Title = "MyTelegram auth server";
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Async(c => c.Console(theme: AnsiConsoleTheme.Code))
    .WriteTo.Async(c => c.File("Logs/startup-log.txt"))
    .CreateLogger();

Log.Information(
    "{Info} {Version}",
    "MyTelegram Auth Server",
    typeof(Program).Assembly.GetName().Version
);
Log.Information(
    "{Description} {Url}",
    "For more information, please visit",
    MyTelegramConsts.RepositoryUrl
);

Log.Information("MyTelegram authentication server starting...");

var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureAppConfiguration(options =>
{
    options.AddEnvironmentVariables();
    options.AddCommandLine(args);
});

builder.UseSerilog(
    (context, configuration) =>
    {
        configuration.ReadFrom.Configuration(context.Configuration)
            .WriteTo.Async(c => c.Console(theme: AnsiConsoleTheme.Code))
            .WriteTo.Async(c => c.File("Logs/log-.txt", rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7));
    }
);
builder.ConfigureServices(
    (context, services) =>
    {
        services.Configure<MyTelegramAuthServerOptions>(
            context.Configuration.GetRequiredSection("App")
        );
        services.Configure<EventBusRabbitMqOptions>(
            context.Configuration.GetRequiredSection("RabbitMQ:EventBus")
        );
        services.Configure<RabbitMqOptions>(
            context.Configuration.GetRequiredSection("RabbitMQ:Connections:Default")
        );
        services.AddHostedService<MyTelegramAuthServerBackgroundService>();
        services.AddAuthServer();
        services.AddMyTelegramStackExchangeRedisCache(options =>
        {
            options.Configuration = context.Configuration.GetValue<string>("Redis:Configuration");
        });
        services.AddCacheJsonSerializer(options =>
        {
            options.TypeInfoResolverChain.Add(MyJsonSerializeContext.Default);
        });

        services.AddMyTelegramRabbitMqEventBus();
    }
);

var app = builder.Build();

_ = app.RunAsync();
Log.Information("Auth server running, waiting...");
Thread.Sleep(Timeout.Infinite);
