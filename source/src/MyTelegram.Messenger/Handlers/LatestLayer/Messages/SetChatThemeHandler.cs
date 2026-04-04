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

        // Extract emoticon from theme
        string emoticon = string.Empty;
        if (obj.Theme is MyTelegram.Schema.TInputChatTheme chatTheme)
        {
            emoticon = chatTheme.Emoticon;
        }

        // Save theme to user_chat_themes collection
        // This is used to return theme in userFull.theme
        var themeCollection = database.GetCollection<BsonDocument>("user_chat_themes");
        var themeFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("PeerType", (int)peer.PeerType),
            Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId)
        );

        if (string.IsNullOrEmpty(emoticon))
        {
            // Remove theme if emoticon is empty (reset to default)
            await themeCollection.DeleteOneAsync(themeFilter);
        }
        else
        {
            // Upsert theme
            var themeUpdate = Builders<BsonDocument>.Update
                .Set("UserId", input.UserId)
                .Set("PeerType", (int)peer.PeerType)
                .Set("PeerId", peer.PeerId)
                .Set("Emoticon", emoticon)
                .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await themeCollection.UpdateOneAsync(themeFilter, themeUpdate, new UpdateOptions { IsUpsert = true });
        }

        // ALSO save theme emoticon to dialog
        // This ensures the theme is properly associated with the dialog
        // and can be retrieved when loading chat info
        var dialogId = DialogId.Create(input.UserId, peer.PeerType, peer.PeerId);
        var dialogCollection = database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
        var dialogFilter = Builders<BsonDocument>.Filter.Eq("_id", dialogId.Value);

        if (string.IsNullOrEmpty(emoticon))
        {
            // Remove theme from dialog
            var dialogUpdate = Builders<BsonDocument>.Update.Unset("ThemeEmoticon");
            await dialogCollection.UpdateOneAsync(dialogFilter, dialogUpdate);
        }
        else
        {
            // Set theme in dialog
            var dialogUpdate = Builders<BsonDocument>.Update.Set("ThemeEmoticon", emoticon);
            await dialogCollection.UpdateOneAsync(dialogFilter, dialogUpdate, new UpdateOptions { IsUpsert = true });
        }

        // Create service message action
        // This is the ONLY service message for theme change
        // (SetChatWallPaperHandler does NOT send a separate message)
        var action = new MyTelegram.Schema.TMessageActionSetChatTheme
        {
            Theme = new MyTelegram.Schema.TChatTheme
            {
                Emoticon = emoticon
            }
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

        // Return null to let event sourcing handle the update delivery
        // The message will arrive via push notification
        return null!;
    }
}
