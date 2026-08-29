namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>
/// Records that the caller used a peer in a way no message expresses — picking an inline result,
/// opening a mini app, finishing a call, forwarding somewhere.
/// See https://corefork.telegram.org/api/top-rating
/// </summary>
/// <remarks>
/// Best effort by construction: a rating is a convenience, so a failed write is logged and swallowed
/// rather than allowed to fail the send, the call or the mini app that triggered it.
/// </remarks>
public interface ITopPeerUsageRecorder
{
    Task RecordAsync(long userId, TopPeerCategory category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a forward into the right one of the two forward categories. tdlib keeps the same split
    /// (<c>TopDialogManager::remove_dialog</c> rewrites <c>ForwardUsers</c> to <c>ForwardChats</c> for a
    /// non-user peer), so the category has to follow the peer type rather than the caller's guess.
    /// </summary>
    Task RecordForwardAsync(long userId, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TopPeerUsageRecorder(
    ITopPeerSettingsStore settingsStore,
    ITopPeerUsageStore usageStore,
    ITopPeerRatingCache cache,
    ILogger<TopPeerUsageRecorder> logger)
    : ITopPeerUsageRecorder, ITransientDependency
{
    public async Task RecordAsync(long userId, TopPeerCategory category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default)
    {
        if (userId == 0 || peerId == 0 || peerType is PeerType.Empty or PeerType.Self)
        {
            return;
        }

        try
        {
            // Opting out has to stop the counting, not just hide it: tdlib stops updating its own copy
            // the moment top peers are disabled, and re-enabling should not surface a month of history
            // the user believed was not being kept.
            if (await settingsStore.IsDisabledAsync(userId, cancellationToken))
            {
                return;
            }

            var exclusions = await settingsStore.GetExclusionsAsync(userId, cancellationToken);
            if (exclusions.IsExcluded(category, peerType, peerId))
            {
                return;
            }

            await usageStore.RecordAsync(userId, category, peerType, peerId,
                (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cancellationToken);

            cache.Invalidate(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to record top peer usage, userId={UserId} category={Category} peer={PeerType}:{PeerId}",
                userId, category, peerType, peerId);
        }
    }

    public Task RecordForwardAsync(long userId, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default)
    {
        var category = peerType == PeerType.User ? TopPeerCategory.ForwardUsers : TopPeerCategory.ForwardChats;

        return RecordAsync(userId, category, peerType, peerId, cancellationToken);
    }
}
