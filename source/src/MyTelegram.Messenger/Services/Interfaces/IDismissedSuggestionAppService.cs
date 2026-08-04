namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Persists which <a href="https://corefork.telegram.org/api/config#suggestions">suggestions</a> a
/// user has dismissed via <c>help.dismissSuggestion</c>, so they are not served again.
/// </summary>
public interface IDismissedSuggestionAppService
{
    /// <summary>
    /// Records that <paramref name="selfUserId"/> dismissed <paramref name="suggestion"/>.
    /// <paramref name="peer"/> is null for global (app config) suggestions and set for the pending
    /// suggestions attached to a channel.
    /// </summary>
    Task DismissAsync(long selfUserId, Peer? peer, string suggestion);

    /// <summary>Suggestions the user dismissed globally (<c>peer == null</c> at dismissal time).</summary>
    Task<HashSet<string>> GetDismissedAsync(long selfUserId);

    /// <summary>Suggestions the user dismissed for a specific peer.</summary>
    Task<HashSet<string>> GetDismissedAsync(long selfUserId, Peer peer);

    /// <summary>
    /// <paramref name="suggestions"/> minus everything the user already dismissed globally, order preserved.
    /// </summary>
    Task<List<string>> FilterDismissedAsync(long selfUserId, IReadOnlyList<string> suggestions);
}
