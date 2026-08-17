using GetChatInviteByLinkQuery = MyTelegram.Queries.GetChatInviteByLinkQuery;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get info about the users that joined the chat using a specific chat invite
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INVITE_HASH_EXPIRED The invite link has expired.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SEARCH_WITH_LINK_NOT_SUPPORTED You cannot provide a search query and an invite link at the same time.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getChatInviteImporters"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetChatInviteImportersHandler(IQueryProcessor queryProcessor, IPeerHelper peerHelper, IUserConverterService userConverterService, IChatInviteLinkHelper chatInviteLinkHelper) : RpcResultObjectHandler<RequestGetChatInviteImporters, IChatInviteImporters>
{
    protected override async Task<IChatInviteImporters> HandleCoreAsync(IRequestInput input, RequestGetChatInviteImporters obj)
    {
        if (obj.Peer is not TInputPeerChannel inputPeerChannel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        if (!string.IsNullOrEmpty(obj.Q) && !string.IsNullOrEmpty(obj.Link))
        {
            RpcErrors.RpcErrors400.SearchWithLinkNotSupported.ThrowRpcError();
        }

        var channelAdminReadModel = await queryProcessor.ProcessAsync(new GetChatAdminQuery(inputPeerChannel.ChannelId, input.UserId));
        if (channelAdminReadModel == null)
        {
            RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
        }

        long? inviteId = null;
        if (!string.IsNullOrEmpty(obj.Link))
        {
            var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(chatInviteLinkHelper.GetHashFromLink(obj.Link)));

            // Invite hashes are global, so the link has to actually belong to the peer the caller
            // is an admin of.
            if (chatInviteReadModel == null || chatInviteReadModel.PeerId != inputPeerChannel.ChannelId)
            {
                RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
            }

            inviteId = chatInviteReadModel!.InviteId;
        }

        // q searches the requesting users by name/username, which the read model stores cannot
        // express, so the matching user ids are resolved up front and used as a filter.
        var searchUserIds = await ResolveSearchUserIdsAsync(obj.Q);
        if (searchUserIds is { Count: 0 })
        {
            return EmptyResult();
        }

        var offsetUserPeer = peerHelper.GetPeer(obj.OffsetUser);
        var offsetUserId = offsetUserPeer.PeerId > 0 ? offsetUserPeer.PeerId : (long?)null;
        var offsetDate = obj.OffsetDate > 0 ? obj.OffsetDate : (int?)null;

        var (importers, count) = obj.Requested
            ? await GetPendingRequestsAsync(inputPeerChannel.ChannelId, inviteId, offsetDate, offsetUserId, searchUserIds, obj.Limit)
            : await GetJoinedImportersAsync(inputPeerChannel.ChannelId, inviteId, offsetDate, offsetUserId, searchUserIds, obj.SubscriptionExpired, obj.Limit);

        var userIds = importers.Select(p => p.UserId).Distinct().ToList();
        var users = await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer);
        var aboutByUserId = await GetAboutByUserIdAsync(userIds);

        foreach (var importer in importers)
        {
            if (importer.About == null && aboutByUserId.TryGetValue(importer.UserId, out var about))
            {
                importer.About = about;
            }
        }

        return new TChatInviteImporters
        {
            Count = count,
            Importers = [.. importers],
            Users = [.. users]
        };
    }

    private static TChatInviteImporters EmptyResult()
    {
        return new TChatInviteImporters
        {
            Count = 0,
            Importers = new TVector<IChatInviteImporter>(),
            Users = new TVector<IUser>()
        };
    }

    private async Task<List<long>?> ResolveSearchUserIdsAsync(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return null;
        }

        var userNameReadModels = await queryProcessor.ProcessAsync(new SearchUserNameQuery(q));
        var userIds = userNameReadModels.Select(p => p.PeerId).ToList();

        var users = await queryProcessor.ProcessAsync(new SearchUserByKeywordQuery(q, MaxSearchResults));
        userIds.AddRange(users.Select(p => p.UserId));

        return userIds.Distinct().ToList();
    }

    private const int MaxSearchResults = 100;

    private async Task<Dictionary<long, string?>> GetAboutByUserIdAsync(List<long> userIds)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var userReadModels = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(userIds));

        return userReadModels.ToDictionary(p => p.UserId, p => p.About);
    }

    private async Task<(List<TChatInviteImporter> Importers, int Count)> GetPendingRequestsAsync(long channelId,
        long? inviteId,
        int? offsetDate,
        long? offsetUserId,
        List<long>? userIds,
        int limit)
    {
        var readModels = await queryProcessor.ProcessAsync(new GetChatInviteImportersQuery(channelId,
            ChatInviteRequestState.WaitingForApproval,
            inviteId,
            offsetDate,
            offsetUserId,
            userIds,
            limit));

        var count = await queryProcessor.ProcessAsync(new GetChatInviteRequestCountQuery(channelId, inviteId, userIds));

        var importers = readModels
            .OrderByDescending(p => p.Date)
            .Select(p => new TChatInviteImporter
            {
                Date = p.Date,
                Requested = true,
                UserId = p.UserId
            })
            .ToList();

        return (importers, count);
    }

    private async Task<(List<TChatInviteImporter> Importers, int Count)> GetJoinedImportersAsync(long channelId,
        long? inviteId,
        int? offsetDate,
        long? offsetUserId,
        List<long>? userIds,
        bool subscriptionExpired,
        int limit)
    {
        var readModels = await queryProcessor.ProcessAsync(new GetChatInviteImporterListQuery(channelId,
            inviteId,
            offsetDate,
            offsetUserId,
            userIds,
            subscriptionExpired,
            limit));

        var count = await queryProcessor.ProcessAsync(new GetChatInviteImporterCountQuery(channelId, inviteId, userIds, subscriptionExpired));

        var importers = readModels
            .OrderByDescending(p => p.Date)
            .Select(p => new TChatInviteImporter
            {
                About = p.About,
                // approved_by is only meaningful for a member that was actually let in by an admin.
                ApprovedBy = p.Approved ? p.ApprovedBy : null,
                Date = p.Date,
                UserId = p.UserId,
                ViaChatlist = p.ViaChatList
            })
            .ToList();

        return (importers, count);
    }
}
