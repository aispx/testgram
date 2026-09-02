using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.WallPapers;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Set a custom <a href="https://corefork.telegram.org/api/wallpapers">wallpaper »</a> in a specific private chat with another user.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// 400 WALLPAPER_NOT_FOUND The specified wallpaper could not be found.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setChatWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The method carries four different intentions, and telling them apart is the whole of it:</para>
/// <list type="bullet">
/// <item><description><b><c>wallpaper</c> given</b> — a manual change. Emits the
/// <c>messageActionSetChatWallPaper</c> service message that invites the other user to apply the same
/// wallpaper.</description></item>
/// <item><description><b><c>id</c> given and <c>wallpaper</c> omitted</b> — the other user accepting that
/// invitation. The wallpaper has to be read out of the service message named by <c>id</c>; clients send
/// no wallpaper at all here (Android <c>ChatThemeController.setWallpaperToPeer</c>, tdlib
/// <c>SetChatWallPaperQuery</c> with <c>ID_MASK</c>). This used to resolve to "no wallpaper" and so
/// <b>removed</b> the wallpaper it was asked to apply. The emitted action carries <c>same</c>, which is
/// what makes clients draw a plain acknowledgment instead of a second invitation.</description></item>
/// <item><description><b><c>revert</c> given</b> — undoing a wallpaper the other side installed with
/// <c>for_both</c>, on this side only. No service message: nothing is being announced to the
/// chat.</description></item>
/// <item><description><b>nothing given</b> — removal.</description></item>
/// </list>
///
/// <para>The response carries the <c>updatePeerWallpaper</c> this produced rather than a fabricated
/// service message. Android applies the wallpaper straight from it
/// (<c>ChatThemeController.processUpdate</c> writes <c>userFull.wallpaper</c> / <c>chatFull.wallpaper</c>
/// and persists it), so the caller sees the change immediately while the service message arrives by push
/// with a real message id.</para>
/// </remarks>
internal sealed class SetChatWallPaperHandler(
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IChatWallPaperService chatWallPaperService,
    IObjectMessageSender objectMessageSender,
    IAccessHashHelper2 accessHashHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IBoostLevelCalculator boostLevelCalculator,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetChatWallPaper, MyTelegram.Schema.IUpdates>
{
    /// <summary>Matches <c>channel_wallpaper_level_min</c> / <c>group_wallpaper_level_min</c>.</summary>
    private const int ChatThemeWallPaperLevelMin = 9;

    /// <summary>
    /// Matches <c>channel_custom_wallpaper_level_min</c> / <c>group_custom_wallpaper_level_min</c>. The
    /// numbers are equal for channels and groups in this deployment's app config, so one constant each.
    /// </summary>
    private const int CustomWallPaperLevelMin = 10;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestSetChatWallPaper obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var isChannel = peer.PeerType == PeerType.Channel;

        if (isChannel)
        {
            // A channel wallpaper is the channel's own, seen by every member, so only an admin allowed to
            // change the channel info may set it.
            await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId, p => p.ChangeInfo);
        }

        // The owner of the record is the channel itself for a channel, and the caller for a private
        // chat, where each side keeps its own wallpaper.
        var ownerId = isChannel ? peer.PeerId : input.UserId;

        if (obj.Revert)
        {
            var reverted = await chatWallPaperService.RevertChatWallPaperAsync(ownerId, peer);

            return await AnnounceAsync(input, peer, reverted, appliesToPeer: false);
        }

        var (wallPaperId, settings, fromServiceMessage) = await ResolveAsync(input, obj);

        MyTelegram.Schema.IWallPaper? wallpaper = null;
        if (wallPaperId.HasValue)
        {
            wallpaper = await chatWallPaperService.GetWallPaperAsync(wallPaperId.Value, settings);
            if (wallpaper == null)
            {
                RpcErrors.RpcErrors400.WallpaperNotFound.ThrowRpcError();
            }

            if (isChannel)
            {
                await CheckBoostLevelAsync(peer.PeerId, wallPaperId.Value, settings);
            }
        }

        await chatWallPaperService.SetChatWallPaperAsync(ownerId, peer, wallPaperId, settings, overridden: false);

        // for_both installs the same wallpaper on the other side of the chat, where it counts as
        // overridden: the peer did not pick it themselves and may revert it. It is meaningless for a
        // channel — "When setting channel wallpapers, do not set the for_both flag" — and no error is
        // documented for sending it anyway, so it is ignored there rather than refused.
        var appliesToPeer = obj.ForBoth && peer.PeerType == PeerType.User && peer.PeerId != input.UserId;
        if (appliesToPeer)
        {
            await chatWallPaperService.SetChatWallPaperAsync(peer.PeerId, new Peer(PeerType.User, input.UserId),
                wallPaperId, settings, overridden: true);
        }

        // The service message belongs to a private chat: it shows the wallpaper and invites the other user
        // to apply it. A channel wallpaper announces itself to every member through updatePeerWallpaper, and
        // a removal has no wallpaper to show.
        if (peer.PeerType == PeerType.User && wallpaper != null)
        {
            await SendServiceMessageAsync(input, peer, wallpaper, same: fromServiceMessage, obj.ForBoth);
        }

        return await AnnounceAsync(input, peer, wallpaper, appliesToPeer);
    }

    /// <summary>
    /// Which wallpaper is being set, and whether it came from the service message named by <c>id</c> —
    /// the flag that becomes <c>messageActionSetChatWallPaper.same</c>.
    /// </summary>
    private async Task<(long? WallPaperId, MyTelegram.Schema.IWallPaperSettings? Settings, bool FromServiceMessage)>
        ResolveAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetChatWallPaper obj)
    {
        if (obj.Wallpaper == null && obj.Id.HasValue)
        {
            var (wallPaperId, storedSettings) = await ReadServiceMessageWallPaperAsync(input, obj.Id.Value);

            return (wallPaperId, obj.Settings ?? storedSettings, true);
        }

        var resolved = await chatWallPaperService.ResolveWallPaperIdAsync(obj.Wallpaper);

        // inputWallPaperNoFile{id = 0} is a wallpaper made of its settings alone. Without settings it
        // names nothing at all.
        if (resolved == 0 && obj.Settings == null)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        return (resolved, obj.Settings, false);
    }

    private async Task<(long WallPaperId, MyTelegram.Schema.IWallPaperSettings? Settings)>
        ReadServiceMessageWallPaperAsync(IRequestInput input, int messageId)
    {
        var message = (await queryProcessor.ProcessAsync(new GetMessagesQuery(
            input.UserId,
            MessageType.Unknown,
            null,
            [messageId],
            0,
            1,
            null,
            null,
            input.UserId,
            0))).FirstOrDefault();

        if (message?.MessageAction is not MyTelegram.Schema.TMessageActionSetChatWallPaper action)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();

            return (0, null);
        }

        return action.Wallpaper switch
        {
            MyTelegram.Schema.TWallPaper paper => (paper.Id, paper.Settings),
            MyTelegram.Schema.TWallPaperNoFile noFile => (noFile.Id, noFile.Settings),
            _ => (0, null)
        };
    }

    /// <summary>
    /// A channel or supergroup has to be boosted before it may carry a wallpaper: the fill wallpapers
    /// <c>account.getChatThemes</c> serves need <c>channel_wallpaper_level_min</c>, anything else needs
    /// <c>channel_custom_wallpaper_level_min</c>. A <c>getChatThemes</c> wallpaper is exactly the one that
    /// names no catalogue row and carries an emoticon, which is what clients send for it.
    /// </summary>
    private async Task CheckBoostLevelAsync(long channelId, long wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings)
    {
        var isChatThemeWallPaper = wallPaperId == 0 && WallPaperSettingsSerializer.EmoticonOf(settings) != null;
        var minLevel = isChatThemeWallPaper ? ChatThemeWallPaperLevelMin : CustomWallPaperLevelMin;

        if (await boostLevelCalculator.GetLevelAsync(channelId) < minLevel)
        {
            RpcErrors.RpcErrors400.BoostsRequired.ThrowRpcError();
        }
    }

    private async Task SendServiceMessageAsync(IRequestInput input, Peer peer,
        MyTelegram.Schema.IWallPaper wallpaper, bool same, bool forBoth)
    {
        var action = new MyTelegram.Schema.TMessageActionSetChatWallPaper
        {
            Same = same,
            ForBoth = forBoth,
            Wallpaper = wallpaper
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            peer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action);

        await messageAppService.SendMessageAsync([sendInput]);
    }

    /// <summary>
    /// Pushes <c>updatePeerWallpaper</c> — "Wallpaper changes will also emit an updatePeerWallpaper
    /// update" — and returns the caller's own copy of it, without which the session that made the change
    /// keeps serving the previous <c>userFull.wallpaper</c> until it refetches.
    /// </summary>
    private async Task<MyTelegram.Schema.IUpdates> AnnounceAsync(IRequestInput input, Peer peer,
        MyTelegram.Schema.IWallPaper? wallpaper, bool appliesToPeer)
    {
        if (peer.PeerType == PeerType.Channel)
        {
            // One wallpaper for the whole channel, so every member hears about it.
            var channelUpdates = WallPaperUpdates(new MyTelegram.Schema.TPeerChannel { ChannelId = peer.PeerId },
                wallpaper, overridden: false);
            await objectMessageSender.PushMessageToPeerAsync(peer, channelUpdates,
                excludeAuthKeyId: input.PermAuthKeyId);

            return channelUpdates;
        }

        var selfUpdates = WallPaperUpdates(new MyTelegram.Schema.TPeerUser { UserId = peer.PeerId }, wallpaper,
            overridden: false);
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), selfUpdates,
            excludeAuthKeyId: input.PermAuthKeyId);

        if (appliesToPeer)
        {
            var peerUpdates = WallPaperUpdates(new MyTelegram.Schema.TPeerUser { UserId = input.UserId }, wallpaper,
                overridden: true);
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, peer.PeerId), peerUpdates);
        }

        return selfUpdates;
    }

    private TUpdates WallPaperUpdates(MyTelegram.Schema.IPeer peer, MyTelegram.Schema.IWallPaper? wallpaper,
        bool overridden)
    {
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(new MyTelegram.Schema.TUpdatePeerWallpaper
            {
                Peer = peer,
                Wallpaper = wallpaper,
                WallpaperOverridden = overridden && wallpaper != null
            }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}
