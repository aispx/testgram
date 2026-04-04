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
    IMongoDatabase database,
    IPtsHelper ptsHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetChatWallPaper, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetChatWallPaper obj)
    {
        // Get target peer
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        MyTelegram.Schema.IWallPaper? wallpaper = null;
        long? wallpaperId = null;

        // Get wallpaper if provided
        if (obj.Wallpaper != null)
        {
            if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaper inputWallpaper)
            {
                wallpaperId = inputWallpaper.Id;
            }
            else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperSlug inputSlug)
            {
                var slugWallpaperCol = database.GetCollection<BsonDocument>("wallpapers");
                var slugFilter = Builders<BsonDocument>.Filter.Eq("Slug", inputSlug.Slug);
                var doc = await slugWallpaperCol.Find(slugFilter).FirstOrDefaultAsync();

                if (doc == null)
                {
                    RpcErrors.RpcErrors400.WallpaperNotFound.ThrowRpcError();
                }

                wallpaperId = doc["WallpaperId"].AsInt64;
            }
            else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperNoFile inputNoFile)
            {
                wallpaperId = inputNoFile.Id;
            }

            if (wallpaperId == 0)
            {
                RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
            }

            // Load wallpaper from database
            var wallpaperCol = database.GetCollection<BsonDocument>("wallpapers");
            var filter = Builders<BsonDocument>.Filter.Eq("WallpaperId", wallpaperId);
            var wallpaperDoc = await wallpaperCol.Find(filter).FirstOrDefaultAsync();

            if (wallpaperDoc == null)
            {
                RpcErrors.RpcErrors400.WallpaperNotFound.ThrowRpcError();
            }

            // Convert to IWallPaper
            if (wallpaperDoc.Contains("DocumentId") && wallpaperDoc["DocumentId"].AsInt64 > 0)
            {
                wallpaper = new MyTelegram.Schema.TWallPaper
                {
                    Id = wallpaperDoc["WallpaperId"].AsInt64,
                    AccessHash = wallpaperDoc.Contains("AccessHash") ? wallpaperDoc["AccessHash"].AsInt64 : 0,
                    Slug = wallpaperDoc.Contains("Slug") ? wallpaperDoc["Slug"].AsString : "",
                    Document = new MyTelegram.Schema.TDocumentEmpty { Id = wallpaperDoc["DocumentId"].AsInt64 },
                    Settings = obj.Settings
                };
            }
            else
            {
                wallpaper = new MyTelegram.Schema.TWallPaperNoFile
                {
                    Id = wallpaperDoc["WallpaperId"].AsInt64,
                    Settings = obj.Settings
                };
            }
        }

        // CRITICAL: Save wallpaper to dialog in database
        // This ensures the wallpaper persists and is returned in userFull.wallpaper
        var dialogId = DialogId.Create(input.UserId, peer.PeerType, peer.PeerId);
        var dialogCollection = database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
        var dialogFilter = Builders<BsonDocument>.Filter.Eq("_id", dialogId.Value);

        if (wallpaperId.HasValue)
        {
            // Set wallpaper
            var update = Builders<BsonDocument>.Update.Set("WallpaperId", wallpaperId.Value);
            await dialogCollection.UpdateOneAsync(dialogFilter, update, new UpdateOptions { IsUpsert = true });
        }
        else
        {
            // Remove wallpaper
            var update = Builders<BsonDocument>.Update.Unset("WallpaperId");
            await dialogCollection.UpdateOneAsync(dialogFilter, update);
        }

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
}
