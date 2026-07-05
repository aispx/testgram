using System.Reflection;
using MyTelegram.Messenger;

namespace MyTelegram.Services.Tests;

public class GetConfigHandlerDcOptionsTests
{
    [Fact]
    public void BuildAdvertisedDcOptions_FillsOfficialClientDcIds()
    {
        var options = new MyTelegramMessengerServerOptions
        {
            ThisDcId = 1,
            DcOptions =
            [
                new DcOption
                {
                    Enabled = true,
                    Id = 1,
                    IpAddress = "127.0.0.1",
                    Port = 20543,
                    MediaOnly = false,
                    ThisPortOnly = true
                },
                new DcOption
                {
                    Enabled = true,
                    Id = 2,
                    IpAddress = "127.0.0.1",
                    Port = 20644,
                    MediaOnly = true,
                    ThisPortOnly = true
                },
                new DcOption
                {
                    Enabled = false,
                    Id = 5,
                    IpAddress = "203.0.113.5",
                    Port = 9999,
                    MediaOnly = false
                }
            ]
        };

        var result = BuildAdvertisedDcOptions(options);

        result.Where(p => !p.MediaOnly).Select(p => p.Id).Distinct().Order().ToArray().ShouldBe([1, 2, 3, 4, 5]);
        result.Where(p => p.MediaOnly).Select(p => p.Id).Distinct().Order().ToArray().ShouldBe([1, 2, 3, 4, 5]);
        result.Any(p => p.Port == 9999).ShouldBeFalse();
        result.Single(p => p.Id == 4 && !p.MediaOnly).Port.ShouldBe(20543);
        result.Single(p => p.Id == 4 && p.MediaOnly).Port.ShouldBe(20644);
    }

    private static List<DcOption> BuildAdvertisedDcOptions(MyTelegramMessengerServerOptions options)
    {
        var handlerType = typeof(MyTelegramMessengerServerOptions).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Help.GetConfigHandler",
            throwOnError: true)!;
        var method = handlerType.GetMethod(
            "BuildAdvertisedDcOptions",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (List<DcOption>)method.Invoke(null, [options])!;
    }
}
