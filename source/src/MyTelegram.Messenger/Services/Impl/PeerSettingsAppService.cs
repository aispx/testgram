namespace MyTelegram.Messenger.Services.Impl;

public class PeerSettingsAppService(IQueryProcessor queryProcessor, IPeerHelper peerHelper, IChannelAppService channelAppService) : IPeerSettingsAppService, ITransientDependency
{
    /// <summary>
    /// How far back a join request still explains an incoming chat from a chat admin.
    /// </summary>
    private const int RequestChatMaxAge = 7 * 24 * 60 * 60;

    public async Task<PeerSettings> GetAsync(long userId,
        Peer peer)
    {
        if (userId == peer.PeerId || peerHelper.IsBotUser(userId))
        {
            return new PeerSettings();
        }

        var peerSettingsReadModel = await queryProcessor.ProcessAsync(new GetPeerSettingsQuery(userId, peer.PeerId));
        if (peerSettingsReadModel is { HiddenPeerSettingsBar: true })
        {
            return new PeerSettings();
        }

        var settings = peerSettingsReadModel?.PeerSettings != null
            ? new PeerSettings
            {
                AddContact = peerSettingsReadModel.PeerSettings.AddContact,
                BlockContact = peerSettingsReadModel.PeerSettings.BlockContact,
                NeedContactsException = peerSettingsReadModel.PeerSettings.NeedContactsException,
                ReportGeo = peerSettingsReadModel.PeerSettings.ReportGeo,
                ReportSpam = peerSettingsReadModel.PeerSettings.ReportSpam,
                ShareContact = peerSettingsReadModel.PeerSettings.ShareContact,
            }
            : new PeerSettings();

        await ApplyRequestChatInfoAsync(settings, userId, peer);

        return settings;
    }

    /// <summary>
    /// Tells the user that this conversation was started by an admin of a chat they recently
    /// requested to join, so clients can show the corresponding action bar.
    /// See https://corefork.telegram.org/api/invites#join-requests
    /// </summary>
    private async Task ApplyRequestChatInfoAsync(PeerSettings settings, long userId, Peer peer)
    {
        if (peer.PeerType != PeerType.User)
        {
            return;
        }

        var minDate = DateTime.UtcNow.ToTimestamp() - RequestChatMaxAge;
        var joinRequests = await queryProcessor.ProcessAsync(new GetJoinRequestsByUserIdQuery(userId, minDate, RecentJoinRequestLimit));

        foreach (var joinRequest in joinRequests)
        {
            var channelReadModel = await channelAppService.GetAsync(joinRequest.ChannelId);
            if (channelReadModel == null)
            {
                continue;
            }

            // Only the admins of that chat get the action bar; anyone else messaging the user is
            // an ordinary conversation.
            if (channelReadModel.AdminList.All(p => p.UserId != peer.PeerId))
            {
                continue;
            }

            settings.RequestChatBroadcast = channelReadModel.Broadcast;
            settings.RequestChatTitle = channelReadModel.Title;
            settings.RequestChatDate = joinRequest.Date;

            return;
        }
    }

    private const int RecentJoinRequestLimit = 20;

    public Task<IPeerSettingsReadModel?> GetPeerSettingsAsync(long userId, long peerId)
    {
        if (userId == peerId || peerHelper.IsBotUser(peerId))
        {
            return Task.FromResult<IPeerSettingsReadModel?>(null);
        }
        return queryProcessor.ProcessAsync(new GetPeerSettingsQuery(userId, peerId));
    }

    public Task<List<PeerSettings>> GetPeerSettingsListAsync(GetPeerSettingsListInput input)
    {
        return Task.FromResult(new List<PeerSettings>());
    }
}