namespace MyTelegram.Messenger.Services.Folders;

/// <param name="Folder">The importing user's folder.</param>
/// <param name="Invite">The link the folder was imported from.</param>
/// <param name="MissingPeers">
/// Peers the link now carries that are neither in the folder nor dismissed by the user — the
/// <c>missing_peers</c> of <c>chatlists.chatlistUpdates</c>.
/// </param>
public record ChatlistUpdateInfo(
    IDialogFilterReadModel Folder,
    ChatlistInviteDocument Invite,
    List<Peer> MissingPeers
);

/// <summary>
/// Works out what an imported folder is missing compared to the link it came from.
///
/// <para>Users of a shared folder poll <c>chatlists.getChatlistUpdates</c> every
/// <c>chatlist_update_period</c> seconds to pick up chats the owner added later; the three update methods all
/// need the same diff, so it lives here.</para>
/// See https://corefork.telegram.org/api/folders#shared-folders
/// </summary>
public interface IChatlistUpdateResolver
{
    /// <summary>
    /// Resolves the folder named by <c>inputChatlistDialogFilter</c> and diffs it against its link. Throws
    /// <c>FILTER_ID_INVALID</c> when the caller has no such folder and <c>FILTER_NOT_SUPPORTED</c> when the
    /// folder was not imported from a link.
    /// </summary>
    Task<ChatlistUpdateInfo> ResolveAsync(long userId, IInputChatlist chatlist);
}

/// <inheritdoc />
public class ChatlistUpdateResolver(
    IQueryProcessor queryProcessor,
    IChatlistInviteStore chatlistInviteStore,
    IChatlistHiddenUpdateStore hiddenUpdateStore) : IChatlistUpdateResolver, ITransientDependency
{
    public async Task<ChatlistUpdateInfo> ResolveAsync(long userId, IInputChatlist chatlist)
    {
        if (chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var folder = await queryProcessor.ProcessAsync(new GetDialogFilterByIdQuery(userId, chatlistFilter.FilterId));
        if (folder == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        if (!folder!.IsShareableFolder || string.IsNullOrEmpty(folder.ImportedFromSlug))
        {
            // A folder the user built themselves has no link to compare against.
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        var invite = await chatlistInviteStore.GetBySlugAsync(folder.ImportedFromSlug!);
        if (invite == null || invite.Revoked)
        {
            RpcErrors.RpcErrors400.InviteSlugExpired.ThrowRpcError();
        }

        var folderPeerIds = folder.Filter.IncludePeers
            .Concat(folder.Filter.PinnedPeers)
            .Select(p => p.Peer.PeerId)
            .ToHashSet();

        var hiddenPeerIds = await hiddenUpdateStore.GetHiddenPeerIdsAsync(userId, folder.Filter.Id);

        var missingPeers = invite!.ToPeers()
            .Where(p => !folderPeerIds.Contains(p.PeerId) && !hiddenPeerIds.Contains(p.PeerId))
            .ToList();

        return new ChatlistUpdateInfo(folder, invite, missingPeers);
    }
}
