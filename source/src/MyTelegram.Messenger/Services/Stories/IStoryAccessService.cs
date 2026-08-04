using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>What a story mutation requires of the caller.</summary>
public enum StoryRight
{
    Post,
    Edit,
    Delete
}

/// <summary>
/// A viewer's relationship to the owners of the stories being read, loaded once per request so that
/// <see cref="StoryHelper.CanViewStory"/> stays a pure function and privacy evaluation does not issue a
/// query per story.
/// </summary>
public sealed class StoryViewerContext
{
    /// <summary>The viewer.</summary>
    public long UserId { get; init; }

    /// <summary>Whether the viewer has Telegram Premium (for the allow-premium rule).</summary>
    public bool IsPremium { get; init; }

    /// <summary>Owners who have the viewer in their contacts, keyed by owner user id.</summary>
    public HashSet<long> OwnersWhoHaveViewerAsContact { get; init; } = [];

    /// <summary>Owners who have the viewer in their close-friends list, keyed by owner user id.</summary>
    public HashSet<long> OwnersWhoHaveViewerAsCloseFriend { get; init; } = [];

    /// <summary>Stealth mode state of the viewer; views are not recorded while it is active.</summary>
    public StoryStealthDocument? StealthMode { get; init; }

    /// <summary>
    /// True when <paramref name="ownerUserId"/> has the viewer in their contacts. The direction matters:
    /// a story restricted to "my contacts" is about the <em>owner's</em> contact list, not the viewer's.
    /// </summary>
    public bool IsContactOf(long ownerUserId) => OwnersWhoHaveViewerAsContact.Contains(ownerUserId);

    public bool IsCloseFriendOf(long ownerUserId) => OwnersWhoHaveViewerAsCloseFriend.Contains(ownerUserId);

    public bool IsStealthActive(long currentUnixTime) => StealthMode?.IsActive(currentUnixTime) ?? false;

    public static StoryViewerContext Empty(long userId) => new() { UserId = userId };
}

public interface IStoryAccessService
{
    /// <summary>
    /// Resolves the target peer for a story mutation, throwing when the caller may not act as that peer.
    /// Users may only act as themselves; channels require the matching admin right.
    /// </summary>
    Task<(long peerId, int peerType)> ResolveOwnedPeerAsync(IInputPeer? peer, long userId, StoryRight right);

    /// <summary>
    /// Resolves the target peer for a story read. Validates that the peer exists and, for channels, that
    /// the caller can see it; per-story privacy is applied separately via <see cref="FilterVisible"/>.
    /// </summary>
    Task<(long peerId, int peerType)> ResolveReadablePeerAsync(IInputPeer? peer, long userId);

    /// <summary>Loads everything needed to evaluate story privacy for one request.</summary>
    Task<StoryViewerContext> GetViewerContextAsync(long userId, IEnumerable<long>? ownerUserIds = null);

    /// <summary>Drops the stories the viewer may not see.</summary>
    List<StoryDocument> FilterVisible(IEnumerable<StoryDocument> stories, long userId, StoryViewerContext context);

    /// <summary>True when the caller may act as the peer, without throwing.</summary>
    Task<bool> CanActAsPeerAsync(long peerId, int peerType, long userId, StoryRight right);
}
