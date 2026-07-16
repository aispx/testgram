using Microsoft.Extensions.Options;
using MyTelegram.Messenger;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Test helper that supplies the <see cref="IOptionsMonitor{TOptions}"/> the <c>StatsService</c> now
/// requires (it reads <c>MyTelegramMessengerServerOptions.Stats.ReportingWindowDays</c> to compute the
/// reporting period). Provides a fixed snapshot with the configurable reporting window (default 7).
/// </summary>
internal static class StatsTestOptions
{
    public static IOptionsMonitor<MyTelegramMessengerServerOptions> Create(int reportingWindowDays = 7)
    {
        var options = new MyTelegramMessengerServerOptions();
        options.Stats.ReportingWindowDays = reportingWindowDays;
        return new StubOptionsMonitor(options);
    }

    private sealed class StubOptionsMonitor(MyTelegramMessengerServerOptions value)
        : IOptionsMonitor<MyTelegramMessengerServerOptions>
    {
        public MyTelegramMessengerServerOptions CurrentValue { get; } = value;
        public MyTelegramMessengerServerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MyTelegramMessengerServerOptions, string?> listener) => null;
    }
}
