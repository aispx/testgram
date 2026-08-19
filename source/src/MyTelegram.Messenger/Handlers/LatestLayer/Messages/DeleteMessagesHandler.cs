using MongoDB.Bson;

using MongoDB.Driver;

using MyTelegram.Messenger.Services.Mentions;



namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>

/// Deletes messages by their identifiers.

/// Possible errors

/// Code Type Description

/// 403 BOT_ACCESS_FORBIDDEN The specified method <em>can</em> be used over a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business connection</a> for some operations, but the specified query attempted an operation that is not allowed over a business connection.

/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.

/// 403 MESSAGE_DELETE_FORBIDDEN You can't delete one of the messages you tried to delete, most likely because it is a service message.

/// 400 MESSAGE_ID_INVALID The provided message id is invalid.

/// 400 SELF_DELETE_RESTRICTED Business bots can't delete messages just for the user, <code>revoke</code> <strong>must</strong> be set.

/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteMessages"/> </c></para>

/// </summary>

/// <remarks>

/// Access: [User ✔] [Bot ✔] [Anonymous ✖]

/// </remarks>

internal sealed class DeleteMessagesHandler(ICommandBus commandBus, IPtsHelper ptsHelper, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IMentionCleanupService mentionCleanupService, IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteMessages, MyTelegram.Schema.Messages.IAffectedMessages>

{

    protected override async Task<IAffectedMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestDeleteMessages obj)

    {

        if (obj.Id.Count > 0)

        {

            var messageIds = obj.Id.ToList();

            var messageItemsToBeDeletedList = await queryProcessor.ProcessAsync(new GetMessageItemListToBeDeletedQuery(input.UserId, messageIds, obj.Revoke));

            int? newTopMessageId = null;

            int? newTopMessageIdForOtherParticipant = null;

            // Not set top message id for group chat

            if (messageItemsToBeDeletedList.Any(p => p.ToPeerType == PeerType.User))

            {

                newTopMessageId = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(input.UserId, messageIds));

                if (obj.Revoke)

                {

                    var toPeerMessageItem = messageItemsToBeDeletedList.FirstOrDefault(p => p.OwnerUserId != input.UserId);

                    if (toPeerMessageItem != null)

                    {

                        var toPeerMessageIds = messageItemsToBeDeletedList.Where(p => p.OwnerUserId != input.UserId).Select(p => p.MessageId).ToList();

                        newTopMessageIdForOtherParticipant = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(toPeerMessageItem.OwnerUserId, toPeerMessageIds));

                    }

                }

            }



            // Read models are gone once the delete command lands, so the mention counters of everyone
            // mentioned in them have to be settled while the messages still exist.
            await ClearMentionsAsync(messageItemsToBeDeletedList);

            var command = new StartDeleteMessagesCommand(TempId.New, input.ToRequestInfo(), messageItemsToBeDeletedList, obj.Revoke, obj.Revoke, newTopMessageId, newTopMessageIdForOtherParticipant);

            await commandBus.PublishAsync(command);



            // Notify connected business bots about deleted messages

            await NotifyBusinessBotsDeleteAsync(input.UserId, messageItemsToBeDeletedList.ToList());



            return null !;

        }



        var pts = ptsHelper.GetCachedPts(input.UserId);

        return new TAffectedMessages

        {

            Pts = pts,

            PtsCount = 0

        };

    }



    private async Task ClearMentionsAsync(IReadOnlyCollection<MessageItemToBeDeleted> itemsToBeDeleted)

    {

        foreach (var group in itemsToBeDeleted.GroupBy(p => p.OwnerUserId))

        {

            var messages = await queryProcessor.ProcessAsync(

                new GetMessagesByOwnerAndMessageIdListQuery(group.Key, group.Select(p => p.MessageId).ToList()));

            await mentionCleanupService.OnMessagesDeletedAsync(messages);

        }

    }



    private async Task NotifyBusinessBotsDeleteAsync(long userId, List<MessageItemToBeDeleted> deletedMessages)

    {

        if (deletedMessages.Count == 0) return;



        // Group messages by peer

        var messagesByPeer = deletedMessages

            .Where(m => m.ToPeerType == PeerType.User)

            .GroupBy(m => m.ToPeerId);



        foreach (var peerGroup in messagesByPeer)

        {

            var peerId = peerGroup.Key;

            var messageIds = peerGroup.Select(m => m.MessageId).ToList();



            var collection = mongoDatabase.GetCollection<BsonDocument>("connected_business_bots");

            var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);

            var connections = await collection.Find(filter).ToListAsync();



            foreach (var conn in connections)

            {

                var botId = conn["BotId"].AsInt64;

                var connectionId = conn["ConnectionId"].AsString;



                // Check if chat is paused

                var pausedCollection = mongoDatabase.GetCollection<BsonDocument>("paused_business_bot_chats");

                var pausedFilter = Builders<BsonDocument>.Filter.And(

                    Builders<BsonDocument>.Filter.Eq("UserId", userId),

                    Builders<BsonDocument>.Filter.Eq("PeerId", peerId)

                );

                var isPaused = await pausedCollection.Find(pausedFilter).AnyAsync();

                if (isPaused) continue;



                // Check if peer is in ExcludeUsers

                var recipientsDoc = conn["Recipients"].AsBsonDocument;

                if (recipientsDoc.Contains("ExcludeUsers"))

                {

                    var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;

                    if (excludeArray.Any(u => u.AsInt64 == peerId)) continue;

                }



                // Check if bot has ReadMessages right

                var rightsDoc = conn["Rights"].AsBsonDocument;

                if (!rightsDoc.Contains("ReadMessages") || !rightsDoc["ReadMessages"].AsBoolean)

                    continue;



                // Get Qts

                var countersCollection = mongoDatabase.GetCollection<BsonDocument>("counters");

                var qtsFilter = Builders<BsonDocument>.Filter.Eq("_id", $"qts_{botId}");

                var qtsUpdate = Builders<BsonDocument>.Update.Inc("seq", 1);

                var qtsOptions = new FindOneAndUpdateOptions<BsonDocument>

                {

                    IsUpsert = true,

                    ReturnDocument = ReturnDocument.After

                };

                var qtsResult = await countersCollection.FindOneAndUpdateAsync(qtsFilter, qtsUpdate, qtsOptions);

                var qts = qtsResult["seq"].AsInt32;



                // Create updateBotDeleteBusinessMessage

                var updateBotDeleteBusinessMessage = new TUpdateBotDeleteBusinessMessage

                {

                    ConnectionId = connectionId,

                    Peer = new TPeerUser { UserId = peerId },

                    Messages = new TVector<int>(messageIds),

                    Qts = qts

                };



                var botUpdates = new TUpdates

                {

                    Updates = new TVector<IUpdate> { updateBotDeleteBusinessMessage },

                    Users = new TVector<IUser>(),

                    Chats = new TVector<IChat>(),

                    Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()

                };



                await objectMessageSender.PushMessageToPeerAsync(

                    new Peer(PeerType.User, botId),

                    botUpdates,

                    pts: 0

                );

            }

        }

    }

}