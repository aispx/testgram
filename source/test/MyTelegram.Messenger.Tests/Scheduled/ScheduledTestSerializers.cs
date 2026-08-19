using Microsoft.Extensions.DependencyInjection;
using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Runs the same MongoDB serializer registration the servers run, once per test process: it is what
/// teaches the driver to write the <c>_t</c> discriminators of the TL objects stored inside a queued
/// message. Registering it twice throws, so every test class goes through this gate.
/// </summary>
internal static class ScheduledTestSerializers
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        new ServiceCollection().RegisterMongoDbSerializer();
        return true;
    });

    public static void EnsureRegistered() => _ = Registered.Value;
}
