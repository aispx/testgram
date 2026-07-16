namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// Thrown by a stats store when a supplied non-empty pagination offset is not a valid cursor.
/// The Stats_Handler maps this to an invalid-offset RPC error rather than returning a partial page
/// (Requirements 6.8).
/// </summary>
public sealed class InvalidStatsOffsetException(string offset)
    : Exception($"Unrecognized stats pagination offset: '{offset}'.")
{
    public string Offset { get; } = offset;
}
