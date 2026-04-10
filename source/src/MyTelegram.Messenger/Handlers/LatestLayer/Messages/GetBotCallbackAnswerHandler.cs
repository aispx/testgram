using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class GetBotCallbackAnswerHandler(
    IXieFatherBotService xieFatherBotService,
    IPeerHelper peerHelper,
    IMongoDatabase database,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer, MyTelegram.Schema.Messages.IBotCallbackAnswer>
{
    protected override async Task<MyTelegram.Schema.Messages.IBotCallbackAnswer> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer obj)
    {
        Console.WriteLine($"[GetBotCallbackAnswer] Called: userId={input.UserId}, msgId={obj.MsgId}, hasData={obj.Data.HasValue}");

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        Console.WriteLine($"[GetBotCallbackAnswer] Peer: peerId={peer.PeerId}, peerType={peer.PeerType}");

        if (obj.Data.HasValue)
        {
            var data = System.Text.Encoding.UTF8.GetString(obj.Data.Value.Span);
            Console.WriteLine($"[GetBotCallbackAnswer] Callback data: {data}");

            // Handle channel transfer rejection
            if (data.StartsWith("reject_channel_transfer:"))
            {
                await HandleRejectChannelTransferAsync(input, data);
                return new MyTelegram.Schema.Messages.TBotCallbackAnswer
                {
                    CacheTime = 0,
                    Message = "Channel transfer rejected successfully"
                };
            }

            // Handle XieFather bot callbacks
            if (peer.PeerId == XieFatherBotService.BotUserId)
            {
                Console.WriteLine($"[GetBotCallbackAnswer] XieFather callback data: {data}");
                _ = Task.Run(() => xieFatherBotService.HandleCallbackAsync(input, input.UserId, obj.MsgId, data));
            }
        }

        return new MyTelegram.Schema.Messages.TBotCallbackAnswer { CacheTime = 0 };
    }

    private async Task HandleRejectChannelTransferAsync(IRequestInput input, string callbackData)
    {
        // Parse callback data: reject_channel_transfer:{channelId}:{fromUserId}
        var parts = callbackData.Split(':');
        if (parts.Length != 3)
            return;

        if (!long.TryParse(parts[1], out var channelId))
            return;

        if (!long.TryParse(parts[2], out var fromUserId))
            return;

        // Find pending transfer
        var transfersCol = database.GetCollection<BsonDocument>("channel_pending_transfers");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("ToUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FromUserId", fromUserId)
        );

        var transfer = await transfersCol.Find(filter).FirstOrDefaultAsync();
        if (transfer == null)
            return;

        // Delete pending transfer
        await transfersCol.DeleteOneAsync(filter);

        // Get channel info
        var channelCol = database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channel = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();
        if (channel == null)
            return;

        var channelTitle = channel["Title"].AsString;

        // Get new owner name for notification
        var userCol = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var newOwner = await userCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)).FirstOrDefaultAsync();
        var newOwnerName = "User";
        if (newOwner != null)
        {
            newOwnerName = newOwner.Contains("FirstName") ? newOwner["FirstName"].AsString : "User";
            if (newOwner.Contains("LastName") && !newOwner["LastName"].IsBsonNull)
            {
                newOwnerName += " " + newOwner["LastName"].AsString;
            }
        }

        // Send notification to old owner
        var messageText = $"⚠️ Channel Transfer Rejected: {channelTitle}\n\n" +
                         $"You recently transferred ownership of this channel to {newOwnerName}. " +
                         $"The user has rejected the transfer, so the channel has been assigned back to you.";

        var sendInput = new SendMessageInput(
            new RequestInfo(
                ConnectionId: string.Empty,
                SessionId: 0,
                ReqMsgId: 0,
                UserId: 777000, // Telegram service bot
                AccessHashKeyId: 0,
                AuthKeyId: 0,
                PermAuthKeyId: 0,
                RequestId: Guid.NewGuid(),
                Layer: 222,
                Date: DateTime.UtcNow.ToTimestamp(),
                DeviceType: DeviceType.Android
            ),
            777000,
            new Peer(PeerType.User, fromUserId),
            messageText,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text
        );

        await messageAppService.SendMessageAsync([sendInput]);

        Console.WriteLine($"[GetBotCallbackAnswer] Channel transfer rejected: channelId={channelId}, fromUserId={fromUserId}, toUserId={input.UserId}");
    }
}
