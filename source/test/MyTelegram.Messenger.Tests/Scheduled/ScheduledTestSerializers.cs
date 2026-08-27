namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Kept as a thin alias so the scheduled-message tests read as before; the gate itself is shared with
/// every other test that needs the driver's discriminator conventions
/// (see <see cref="MongoDbTestSerializers"/>). Registration is process-global, so there must be exactly
/// one gate.
/// </summary>
internal static class ScheduledTestSerializers
{
    public static void EnsureRegistered() => MongoDbTestSerializers.EnsureRegistered();
}
