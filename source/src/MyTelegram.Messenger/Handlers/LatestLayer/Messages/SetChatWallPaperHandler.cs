using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;
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
/// IMPORTANT: This handler is called in TWO scenarios:
///
/// 1. **Manual wallpaper change** (user explicitly changes wallpaper):
///    - Should send service message "User changed wallpaper"
///    - Indicators: obj.Revert == true OR obj.Id.HasValue (revert to previous message)
///
/// 2. **Automatic call after theme change** (client sets wallpaper as part of theme):
///    - Should NOT send service message (theme handler already sent "User changed theme")
///    - Indicators: obj.Revert == false AND obj.Id == null
///
/// Since we cannot modify the client, we use this heuristic to avoid duplicate messages.
/// </remarks>
internal sealed class SetChatWallPaperHandler(
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IChatWallPaperService chatWallPaperService,
    IObjectMessageSender objectMessageSender,
    IAccessHashHelper2 accessHashHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IPtsHelper ptsHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetChatWallPaper, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetChatWallPaper obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);

        // Get target peer
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // A channel wallpaper is the channel's own, seen by every member, so only an admin allowed to
        // change the channel info may set it.
        if (peer.PeerType == PeerType.Channel)
        {
            await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId, p => p.ChangeInfo);
        }

        var wallpaperId = await chatWallPaperService.ResolveWallPaperIdAsync(obj.Wallpaper);
        MyTelegram.Schema.IWallPaper? wallpaper = null;

        if (wallpaperId.HasValue)
        {
            wallpaper = await chatWallPaperService.GetWallPaperAsync(wallpaperId.Value, obj.Settings);
            if (wallpaper == null)
            {
                RpcErrors.RpcErrors400.WallpaperNotFound.ThrowRpcError();
            }
        }

        // The owner of the record is the channel itself for a channel, and the caller for a private
        // chat, where each side keeps its own wallpaper.
        var ownerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        await chatWallPaperService.SetChatWallPaperAsync(ownerId, peer, wallpaperId, obj.Settings, overridden: false);

        // for_both installs the same wallpaper on the other side of the chat, where it counts as
        // overridden: the peer did not pick it themselves and may revert it.
        // See https://corefork.telegram.org/api/wallpapers#installing-wallpapers-in-a-specific-chat-or-channel
        var appliesToPeer = obj.ForBoth && peer.PeerType == PeerType.User && peer.PeerId != input.UserId;
        if (appliesToPeer)
        {
            await chatWallPaperService.SetChatWallPaperAsync(peer.PeerId, new Peer(PeerType.User, input.UserId),
                wallpaperId, obj.Settings, overridden: true);
        }

        await NotifyWallPaperChangedAsync(input, peer, wallpaper, appliesToPeer);

        // Determine if we should send a service message
        // Send message ONLY when:
        // 1. obj.Revert == true (user is reverting to previous wallpaper)
        // 2. obj.Id.HasValue (user is setting wallpaper from a specific message)
        //
        // Do NOT send message when:
        // - This is an automatic call after setChatTheme (obj.Revert == false && obj.Id == null)
        //
        // This prevents duplicate messages when user sets a theme (which includes wallpaper).
        bool shouldSendServiceMessage = obj.Revert || obj.Id.HasValue;

        if (shouldSendServiceMessage)
        {
            // Manual wallpaper change - send service message
            var action = new MyTelegram.Schema.TMessageActionSetChatWallPaper
            {
                Same = obj.Revert,
                ForBoth = obj.ForBoth,
                Wallpaper = wallpaper ?? new MyTelegram.Schema.TWallPaperNoFile { Id = 0 }
            };

            // Send service message
            var sendInput = new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                peer,
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action
            );

            await messageAppService.SendMessageAsync([sendInput]);

            // Create immediate response with UpdateNewMessage
            var pts = await ptsHelper.IncrementPtsAsync(input.UserId, ptsHelper.GetCachedPts(input.UserId));

            MyTelegram.Schema.IPeer peerObj;
            if (peer.PeerType == PeerType.User)
            {
                peerObj = new MyTelegram.Schema.TPeerUser { UserId = peer.PeerId };
            }
            else if (peer.PeerType == PeerType.Chat)
            {
                peerObj = new MyTelegram.Schema.TPeerChat { ChatId = peer.PeerId };
            }
            else
            {
                peerObj = new MyTelegram.Schema.TPeerChannel { ChannelId = peer.PeerId };
            }

            var serviceMsg = new MyTelegram.Schema.TMessageService
            {
                Id = pts,
                FromId = new MyTelegram.Schema.TPeerUser { UserId = input.UserId },
                PeerId = peerObj,
                Date = CurrentDate,
                Action = action,
                Out = true,
            };

            return new TUpdates
            {
                Updates = new TVector<IUpdate> { new MyTelegram.Schema.TUpdateNewMessage { Message = serviceMsg, Pts = pts, PtsCount = 1 } },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = CurrentDate,
                Seq = 0
            };
        }
        else
        {
            // Automatic call from theme change - do NOT send service message
            // (SetChatThemeHandler already sent "User changed theme" message)
            // Just return empty Updates - wallpaper is already saved in DB
            return new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = CurrentDate,
                Seq = 0
            };
        }
    }

    /// <summary>
    /// Pushes <c>updatePeerWallpaper</c>, without which the caller's other sessions and the peer keep
    /// serving the previous <c>userFull.wallpaper</c>.
    /// See https://corefork.telegram.org/api/peers#handling-updates
    /// </summary>
    private async Task NotifyWallPaperChangedAsync(IRequestInput input, Peer peer,
        MyTelegram.Schema.IWallPaper? wallpaper, bool appliesToPeer)
    {
        if (peer.PeerType == PeerType.Channel)
        {
            // One wallpaper for the whole channel, so every member hears about it.
            var channelUpdates = WallPaperUpdates(new MyTelegram.Schema.TPeerChannel { ChannelId = peer.PeerId },
                wallpaper, overridden: false);
            await objectMessageSender.PushMessageToPeerAsync(peer, channelUpdates,
                excludeAuthKeyId: input.AuthKeyId);

            return;
        }

        var selfUpdates = WallPaperUpdates(new MyTelegram.Schema.TPeerUser { UserId = peer.PeerId }, wallpaper,
            overridden: false);
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), selfUpdates,
            excludeAuthKeyId: input.AuthKeyId);

        if (appliesToPeer)
        {
            var peerUpdates = WallPaperUpdates(new MyTelegram.Schema.TPeerUser { UserId = input.UserId }, wallpaper,
                overridden: true);
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, peer.PeerId), peerUpdates);
        }
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
