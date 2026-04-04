using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Change the chat theme of a certain chat, see <a href="https://corefork.telegram.org/api/themes#chat-themes">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 EMOJI_INVALID The specified theme emoji is valid.
/// 400 EMOJI_NOT_MODIFIED The theme wasn't changed.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setChatTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// IMPORTANT: This is the PRIMARY handler for chat theme changes.
/// It sends the service message "User changed theme" and saves the theme to database.
/// The client will also call messages.setChatWallPaper after this, but that handler
/// does NOT send a separate message to avoid duplication.
/// </remarks>
internal sealed class SetChatThemeHandler(
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetChatTheme, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetChatTheme obj)
    {
        // Get target peer
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // Extract theme info from InputChatTheme
        string? emoticon = null;
        string? giftSlug = null;
        bool isEmpty = false;

        if (obj.Theme is MyTelegram.Schema.TInputChatTheme chatTheme)
        {
            // Regular emoji-based theme
            emoticon = chatTheme.Emoticon;
        }
        else if (obj.Theme is MyTelegram.Schema.TInputChatThemeUniqueGift uniqueGiftTheme)
        {
            // NFT gift-based theme
            giftSlug = uniqueGiftTheme.Slug;

            // Verify user owns this gift
            var savedGiftsCol = database.GetCollection<BsonDocument>("saved-star-gifts");
            var uniqueGiftsCol = database.GetCollection<BsonDocument>("unique-star-gifts");

            var uniqueGift = await uniqueGiftsCol.Find(
                Builders<BsonDocument>.Filter.Eq("Slug", giftSlug)
            ).FirstOrDefaultAsync();

            if (uniqueGift == null)
            {
                RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
            }

            var savedGift = await savedGiftsCol.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("UniqueGiftId", uniqueGift["UniqueGiftId"].AsInt64)
                )
            ).FirstOrDefaultAsync();

            if (savedGift == null)
            {
                RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
            }

            // Check if theme is already in use in another chat
            var dialogCol = database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
            var existingDialog = await dialogCol.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ThemeGiftSlug", giftSlug),
                    Builders<BsonDocument>.Filter.Ne("UserId", input.UserId)
                )
            ).FirstOrDefaultAsync();

            if (existingDialog != null)
            {
                // Remove theme from old chat
                await dialogCol.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", existingDialog["_id"]),
                    Builders<BsonDocument>.Update.Unset("ThemeGiftSlug")
                );
            }
        }
        else if (obj.Theme is MyTelegram.Schema.TInputChatThemeEmpty)
        {
            // Remove theme
            isEmpty = true;
        }

        // Save theme to user_chat_themes collection
        // This is used to return theme in userFull.theme
        var themeCollection = database.GetCollection<BsonDocument>("user_chat_themes");
        var themeFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("PeerType", (int)peer.PeerType),
            Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId)
        );

        if (isEmpty)
        {
            // Remove theme (reset to default)
            await themeCollection.DeleteOneAsync(themeFilter);
        }
        else
        {
            // Upsert theme
            var themeUpdate = Builders<BsonDocument>.Update
                .Set("UserId", input.UserId)
                .Set("PeerType", (int)peer.PeerType)
                .Set("PeerId", peer.PeerId)
                .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            if (!string.IsNullOrEmpty(emoticon))
            {
                themeUpdate = themeUpdate.Set("Emoticon", emoticon).Unset("GiftSlug");
            }
            else if (!string.IsNullOrEmpty(giftSlug))
            {
                themeUpdate = themeUpdate.Set("GiftSlug", giftSlug).Unset("Emoticon");
            }

            await themeCollection.UpdateOneAsync(themeFilter, themeUpdate, new UpdateOptions { IsUpsert = true });
        }

        // ALSO save theme to dialog
        // This ensures the theme is properly associated with the dialog
        // and can be retrieved when loading chat info
        var dialogId = DialogId.Create(input.UserId, peer.PeerType, peer.PeerId);
        var dialogCollection = database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
        var dialogFilter = Builders<BsonDocument>.Filter.Eq("_id", dialogId.Value);

        if (isEmpty)
        {
            // Remove theme from dialog
            var dialogUpdate = Builders<BsonDocument>.Update
                .Unset("ThemeEmoticon")
                .Unset("ThemeGiftSlug");
            await dialogCollection.UpdateOneAsync(dialogFilter, dialogUpdate);
        }
        else if (!string.IsNullOrEmpty(emoticon))
        {
            // Set emoji theme in dialog
            var dialogUpdate = Builders<BsonDocument>.Update
                .Set("ThemeEmoticon", emoticon)
                .Unset("ThemeGiftSlug");
            await dialogCollection.UpdateOneAsync(dialogFilter, dialogUpdate, new UpdateOptions { IsUpsert = true });
        }
        else if (!string.IsNullOrEmpty(giftSlug))
        {
            // Set gift theme in dialog
            var dialogUpdate = Builders<BsonDocument>.Update
                .Set("ThemeGiftSlug", giftSlug)
                .Unset("ThemeEmoticon");
            await dialogCollection.UpdateOneAsync(dialogFilter, dialogUpdate, new UpdateOptions { IsUpsert = true });
        }

        // Create service message action
        // This is the ONLY service message for theme change
        // (SetChatWallPaperHandler does NOT send a separate message)
        MyTelegram.Schema.IChatTheme themeObj;

        if (!string.IsNullOrEmpty(emoticon))
        {
            themeObj = new MyTelegram.Schema.TChatTheme { Emoticon = emoticon };
        }
        else if (!string.IsNullOrEmpty(giftSlug))
        {
            // Load gift for theme
            var uniqueGiftsCol = database.GetCollection<BsonDocument>("unique-star-gifts");
            var giftDoc = await uniqueGiftsCol.Find(
                Builders<BsonDocument>.Filter.Eq("Slug", giftSlug)
            ).FirstOrDefaultAsync();

            if (giftDoc != null)
            {
                var starGift = new MyTelegram.Schema.TStarGiftUnique
                {
                    ThemeAvailable = true,
                    Id = giftDoc["UniqueGiftId"].ToInt64(),
                    GiftId = giftDoc["GiftId"].ToInt64(),
                    Title = giftDoc["Title"].AsString,
                    Slug = giftDoc["Slug"].AsString,
                    Num = giftDoc["Number"].AsInt32,
                    Attributes = new TVector<MyTelegram.Schema.IStarGiftAttribute>(),
                    AvailabilityIssued = giftDoc["AvailabilityIssued"].AsInt32,
                    AvailabilityTotal = giftDoc["AvailabilityTotal"].AsInt32
                };

                var themeSettings = new TVector<MyTelegram.Schema.IThemeSettings>();
                if (giftDoc.Contains("ThemeSettings") && giftDoc["ThemeSettings"].IsBsonArray)
                {
                    foreach (var settingDoc in giftDoc["ThemeSettings"].AsBsonArray)
                    {
                        var setting = settingDoc.AsBsonDocument;
                        themeSettings.Add(new MyTelegram.Schema.TThemeSettings
                        {
                            BaseTheme = setting["BaseTheme"].AsString == "classic"
                                ? new MyTelegram.Schema.TBaseThemeClassic()
                                : new MyTelegram.Schema.TBaseThemeNight(),
                            AccentColor = setting["AccentColor"].ToInt32(),
                            MessageColorsAnimated = true,
                            MessageColors = new TVector<int>(setting["MessageColors"].AsBsonArray.Select(c => c.ToInt32()))
                        });
                    }
                }

                themeObj = new MyTelegram.Schema.TChatThemeUniqueGift
                {
                    Gift = starGift,
                    ThemeSettings = themeSettings
                };
            }
            else
            {
                themeObj = new MyTelegram.Schema.TChatTheme { Emoticon = "" };
            }
        }
        else
        {
            themeObj = new MyTelegram.Schema.TChatTheme { Emoticon = "" };
        }

        var action = new MyTelegram.Schema.TMessageActionSetChatTheme
        {
            Theme = themeObj
        };

        // Send service message via event sourcing
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

        // Return empty Updates - message will arrive via event sourcing
        // Do NOT return null! as it causes client to retry the request
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
