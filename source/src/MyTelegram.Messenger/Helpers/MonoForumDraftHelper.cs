namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// The drafts a user holds in the topics of a <a href="https://corefork.telegram.org/api/monoforum">
/// monoforum</a>. A monoforum topic is addressed by the peer it belongs to rather than by a message id,
/// so its draft is keyed by <c>monoforum_peer_id</c> and travels in
/// <c>updateDraftMessage.saved_peer_id</c> / <c>monoForumDialog.draft</c>.
/// See https://corefork.telegram.org/api/drafts
/// </summary>
internal static class MonoForumDraftHelper
{
    /// <summary>The drafts by the peer id of the topic they belong to.</summary>
    public static async Task<Dictionary<long, IDraftMessage>> GetTopicDraftsAsync(
        IQueryProcessor queryProcessor,
        IDraftConverterService draftConverterService,
        long selfUserId,
        long monoforumChannelId,
        int layer)
    {
        var drafts = await queryProcessor.ProcessAsync(
            new GetDraftListByPeerQuery(selfUserId, PeerType.Channel, monoforumChannelId));

        var result = new Dictionary<long, IDraftMessage>();
        foreach (var draft in drafts)
        {
            var savedPeerId = draft.Draft?.SavedPeerId;
            if (savedPeerId == null)
            {
                continue;
            }

            result[savedPeerId.PeerId] = draftConverterService.ToDraftMessage(draft, layer);
        }

        return result;
    }
}
