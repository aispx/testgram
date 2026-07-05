using System.Threading;

namespace MyTelegram.Messenger.Services;

internal sealed record TakeoutSessionScope(long TakeoutId, bool Contacts);

internal static class TakeoutContext
{
    private sealed class Scope(TakeoutSessionScope? previous) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
        }
    }

    private static readonly AsyncLocal<TakeoutSessionScope?> Current = new();

    public static TakeoutSessionScope? CurrentSession => Current.Value;

    public static IDisposable Enter(TakeoutSessionScope scope)
    {
        var previous = Current.Value;
        Current.Value = scope;
        return new Scope(previous);
    }
}
