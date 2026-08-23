namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Where a chat history may be imported into, and the confirmation text shown before it happens.
/// See https://corefork.telegram.org/api/import
/// </summary>
public interface IHistoryImportPeerValidator
{
    /// <summary>
    /// Throws the documented RPC error unless <paramref name="selfUserId"/> may import a history into
    /// <paramref name="peer"/>, and returns the display name of the destination.
    /// </summary>
    /// <param name="allowLegacyChat">
    /// True for <c>messages.checkHistoryImportPeer</c>, which the clients call on a basic group before
    /// converting it to a supergroup. The rest of the flow only accepts the converted supergroup.
    /// </param>
    Task<string> ValidateAsync(long selfUserId, Peer peer, bool allowLegacyChat = false);

    /// <summary>Text shown by the client in the import confirmation prompt.</summary>
    string BuildConfirmText(Peer peer, string peerTitle);
}

/// <inheritdoc />
public class HistoryImportPeerValidator(
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IQueryProcessor queryProcessor)
    : IHistoryImportPeerValidator, ITransientDependency
{
    public async Task<string> ValidateAsync(long selfUserId, Peer peer, bool allowLegacyChat = false)
    {
        switch (peer.PeerType)
        {
            case PeerType.User:
                return await ValidateUserAsync(selfUserId, peer.PeerId);
            case PeerType.Channel:
                return await ValidateChannelAsync(selfUserId, peer.PeerId);
            case PeerType.Chat when allowLegacyChat:
                // A basic group has no supergroup history to import into yet; the client converts it
                // right after the confirmation and comes back with the new channel.
                return string.Empty;
            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                return string.Empty;
        }
    }

    public string BuildConfirmText(Peer peer, string peerTitle)
    {
        var target = string.IsNullOrWhiteSpace(peerTitle) ? "this chat" : $"\"{peerTitle}\"";

        return peer.PeerType == PeerType.User
            ? $"Do you want to import messages from another app into the chat with {target}? " +
              "The messages will be added to this chat and marked as imported."
            : $"Do you want to import messages from another app into the group {target}? " +
              "The messages will be added to this group and marked as imported.";
    }

    private async Task<string> ValidateUserAsync(long selfUserId, long targetUserId)
    {
        if (targetUserId == selfUserId)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // The nullable overload: the other one throws when the id does not exist, which would surface
        // as an internal error instead of USER_ID_INVALID.
        var user = await userAppService.GetAsync((long?)targetUserId);
        if (user == null || user.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (user!.Bot)
        {
            RpcErrors.RpcErrors400.UserIsBot.ThrowRpcError();
        }

        // "history imports are allowed for private chats with a mutual contact": the address book
        // entry has to exist in both directions.
        var forward = await queryProcessor.ProcessAsync(new GetContactQuery(selfUserId, targetUserId));
        var backward = await queryProcessor.ProcessAsync(new GetContactQuery(targetUserId, selfUserId));
        if (forward == null || backward == null)
        {
            RpcErrors.RpcErrors400.UserNotMutualContact.ThrowRpcError();
        }

        return string.IsNullOrWhiteSpace(user.LastName)
            ? user.FirstName
            : $"{user.FirstName} {user.LastName}".Trim();
    }

    private async Task<string> ValidateChannelAsync(long selfUserId, long channelId)
    {
        var channel = await channelAppService.GetAsync((long?)channelId);
        if (channel == null || channel.IsDeleted)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        // Only supergroups can hold an imported history; a broadcast channel is not a chat.
        if (!channel!.MegaGroup)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(channelId, selfUserId, p => p.ChangeInfo,
            RpcErrors.RpcErrors400.ChatAdminRequired);

        return channel.Title;
    }
}
