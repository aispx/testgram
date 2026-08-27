namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Return all message <a href="https://corefork.telegram.org/api/drafts">drafts</a>.<br/>
/// Returns all the latest <a href="https://corefork.telegram.org/constructor/updateDraftMessage">updateDraftMessage</a> updates related to all chats with drafts.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getAllDrafts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The peers travel with the updates. TDLib feeds this answer straight into its update manager
/// and, for a draft in a dialog it does not know yet, repairs it with <c>messages.getPeerDialogs</c> —
/// but only if it has read access to the peer, which means only if the user or the channel came with
/// the answer. A draft in a chat with no history is exactly that case.</para>
/// </remarks>
internal sealed class GetAllDraftsHandler(
    IQueryProcessor queryProcessor,
    IUpdatesConverterService updatesConverterService,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAllDrafts, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestGetAllDrafts obj)
    {
        var draftReadModels = await queryProcessor.ProcessAsync(new GetAllDraftQuery(input.UserId));
        var peers = draftReadModels
            .Where(p => p.Peer != null)
            .Select(p => p.Peer)
            .ToList();

        // PeerType.Self is the Saved Messages chat: still a user, and its own draft.
        var userIds = peers.Where(p => p.PeerType is PeerType.User or PeerType.Self)
            .Select(p => p.PeerId).Distinct().ToList();
        var channelIds = peers.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId).Distinct().ToList();

        List<ILayeredUser> users = userIds.Count > 0
            ? await userConverterService.GetUserListAsync(input, userIds, layer: input.Layer)
            : [];
        List<IChat> chats = channelIds.Count > 0
            ? await chatConverterService.GetChannelListAsync(input, channelIds, layer: input.Layer)
            : [];

        return updatesConverterService.ToDraftsUpdates(draftReadModels, input.Layer, users, chats);
    }
}
