using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// The Access_Controller: rejects bot/anonymous callers, resolves the target channel/peer, and
/// applies channel-kind, joinability, and admin checks in the fixed order defined by Requirement 1.5.
/// The first failing check throws its RPC error.
/// </summary>
public interface IStatsAccessController
{
    /// <summary>
    /// Rejects bot/anonymous callers; resolves the channel (<c>CHANNEL_INVALID</c> when missing); applies the
    /// required kind (<c>BROADCAST_REQUIRED</c>/<c>MEGAGROUP_REQUIRED</c>); optionally applies joinability
    /// (<c>CHANNEL_PRIVATE</c>); then admin rights (<c>CHAT_ADMIN_REQUIRED</c>). Returns the resolved channel.
    /// </summary>
    Task<IChannelReadModel> ResolveChannelForStatsAsync(IRequestInput input, IInputChannel channel, StatsChannelKind requiredKind, bool checkJoinable);

    /// <summary>
    /// Rejects bot/anonymous callers; resolves an <c>InputPeer</c> for story methods
    /// (<c>PEER_ID_INVALID</c> when missing) and verifies authorship/admin rights. Returns the resolved peer.
    /// </summary>
    Task<Peer> ResolvePeerForStoryStatsAsync(IRequestInput input, IInputPeer peer);
}
