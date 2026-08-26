using Microsoft.Extensions.DependencyInjection;
using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.Messenger.Tests;

/// <summary>
/// Runs the same MongoDB serializer registration the servers run, once per test process: it is what
/// teaches the driver to write and read the <c>_t</c> discriminators of TL objects stored inside a read
/// model — the attributes of a sticker document, the media of a queued message. Registration is global
/// process state, so every test class goes through this one gate rather than calling it again.
/// </summary>
internal static class MongoDbTestSerializers
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        new ServiceCollection().RegisterMongoDbSerializer();
        return true;
    });

    public static void EnsureRegistered() => _ = Registered.Value;
}
