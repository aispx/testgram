using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Toggle autotranslation in a channel, for all users: see <a href="https://corefork.telegram.org/api/translation#autotranslation-for-channels">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.toggleAutotranslation"/> </c></para>
/// </summary>
/// <remarks>
/// <para>Sets <c>channel.autotranslation</c> (flags2.15) — the channel-wide setting. It is not
/// <c>channelFull.translations_disabled</c>, which is the per-user popup-hide flag written by
/// <c>messages.togglePeerTranslations</c>; this handler used to write that one, directly into
/// <c>eventflow-channelreadmodel</c>, so it stored the wrong thing in the wrong place and nothing ever
/// read it back.</para>
///
/// <para>The channel must have reached <c>channel_autotranslation_level_min</c> boosts. The gate only
/// applies when switching it <b>on</b>: a channel that has since lost boosts must still be able to turn
/// it off.</para>
///
/// <para>Answered by <c>ChannelDomainEventHandler</c>, which also pushes <c>updateChannel</c> — hence
/// <c>null!</c>. Returning a fabricated empty <c>TUpdates</c> here, as this used to, left every other
/// session unaware of the change.</para>
///
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleAutotranslationHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IBoostLevelCalculator boostLevelCalculator,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleAutotranslation, MyTelegram.Schema.IUpdates>
{
    /// <summary>Matches <c>channel_autotranslation_level_min</c> in the app config.</summary>
    private const int ChannelAutotranslationLevelMin = 3;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestToggleAutotranslation obj)
    {
        var peer = ResolveChannel(obj, input.UserId);
        var channelId = peer.PeerId;

        // change_info, like every sibling toggle: clients put this switch on the channel edit screen,
        // which an admin with change-info reaches, not only the owner.
        await channelAdminRightsChecker.CheckAdminRightAsync(channelId, input.UserId, p => p.ChangeInfo,
            RpcErrors.RpcErrors403.ChatAdminRequired);

        var channel = await channelAppService.GetAsync(channelId);

        // Only broadcast channels autotranslate. No client offers the switch for a supergroup, and a
        // flag stored on one would be reported to clients that have nowhere to draw it.
        if (channel is not { Broadcast: true })
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        if (obj.Enabled && await boostLevelCalculator.GetLevelAsync(channelId) < ChannelAutotranslationLevelMin)
        {
            RpcErrors.RpcErrors400.BoostsRequired.ThrowRpcError();
        }

        await commandBus.PublishAsync(new ToggleAutotranslationCommand(ChannelId.Create(channelId),
            input.ToRequestInfo(), obj.Enabled));

        await AdminLogHelper.LogToggleAutotranslation(mongoDatabase, channelId, input.UserId, obj.Enabled);

        return null!;
    }

    /// <summary>
    /// Both <c>InputChannel</c> forms carry an access hash to check, which <see cref="IPeerHelper"/>
    /// does; <c>inputChannelEmpty</c> names nothing.
    /// </summary>
    private Peer ResolveChannel(MyTelegram.Schema.Channels.RequestToggleAutotranslation obj, long selfUserId)
    {
        IInputPeer? inputPeer = obj.Channel switch
        {
            TInputChannel inputChannel => new TInputPeerChannel
            {
                ChannelId = inputChannel.ChannelId,
                AccessHash = inputChannel.AccessHash
            },
            TInputChannelFromMessage fromMessage => new TInputPeerChannelFromMessage
            {
                Peer = fromMessage.Peer,
                MsgId = fromMessage.MsgId,
                ChannelId = fromMessage.ChannelId
            },
            _ => null
        };

        var peer = inputPeer == null ? null : peerHelper.GetPeer(inputPeer, selfUserId);

        if (peer is not { PeerType: PeerType.Channel })
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        return peer!;
    }
}
