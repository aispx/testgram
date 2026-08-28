using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Returns identifiers of pinned or always included chats from a chat folder imported using a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>, which are suggested to be left when the chat folder is deleted.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.getLeaveChatlistSuggestions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Only groups and channels are suggested — a private chat cannot be left — and only those that live in
/// no other folder of the user, since a chat they filed somewhere else is one they want to keep. The client
/// pre-marks whatever comes back in the deletion dialog, so suggesting a chat that cannot be left, or one the
/// user organised separately, deletes it from their account on a single tap.</para>
/// </remarks>
internal sealed class GetLeaveChatlistSuggestionsHandler(IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<RequestGetLeaveChatlistSuggestions, TVector<IPeer>>
{
    protected override async Task<TVector<IPeer>> HandleCoreAsync(IRequestInput input,
        RequestGetLeaveChatlistSuggestions obj)
    {
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var filters = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(input.UserId));
        var folder = filters.FirstOrDefault(p => p.Filter.Id == chatlistFilter.FilterId);
        if (folder == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        if (!folder!.IsShareableFolder)
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        var peerIdsInOtherFolders = filters
            .Where(p => p.Filter.Id != folder.Filter.Id)
            .SelectMany(p => p.Filter.IncludePeers.Concat(p.Filter.PinnedPeers))
            .Select(p => p.Peer.PeerId)
            .ToHashSet();

        var suggestions = new TVector<IPeer>();
        var seen = new HashSet<long>();
        foreach (var inputPeer in folder.Filter.PinnedPeers.Concat(folder.Filter.IncludePeers))
        {
            var peer = inputPeer.Peer;
            if (peer.PeerType is not (PeerType.Channel or PeerType.Chat))
            {
                continue;
            }

            if (peerIdsInOtherFolders.Contains(peer.PeerId) || !seen.Add(peer.PeerId))
            {
                continue;
            }

            suggestions.Add(new Peer(peer.PeerType, peer.PeerId).ToPeer());
        }

        return suggestions;
    }
}
