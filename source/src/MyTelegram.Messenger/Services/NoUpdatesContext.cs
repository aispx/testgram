using System.Threading;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// AsyncLocal scope used by <c>invokeWithoutUpdates</c> to flag that updates must not be
/// generated/delivered as a side effect of the current request. Mirrors the pattern used by
/// <see cref="TakeoutContext"/>. Downstream update-producing services can consult
/// <see cref="IsSuppressed"/> to decide whether to push updates to the current connection.
/// </summary>
internal static class NoUpdatesContext
{
    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
        }
    }

    private static readonly AsyncLocal<bool> Current = new();

    public static bool IsSuppressed => Current.Value;

    public static IDisposable Enter()
    {
        var previous = Current.Value;
        Current.Value = true;
        return new Scope(previous);
    }
}
