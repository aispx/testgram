using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Discussion;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Stop getting notifications about <a href="https://corefork.telegram.org/api/discussion">discussion replies</a> of a certain user in <code>@replies</code>
/// Possible errors
/// Code Type Description
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.blockFromReplies"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class BlockFromRepliesHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IRepliesBlockService repliesBlockService) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestBlockFromReplies, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestBlockFromReplies obj)
    {
        // msg_id names a message in the caller's own chat with @replies; the commenter to block is the
        // one the mirrored message was forwarded from.
        // See https://corefork.telegram.org/api/discussion#replies
        var messageReadModel = await queryProcessor.ProcessAsync(
            new GetMessageByIdQuery(MessageId.Create(input.UserId, obj.MsgId).Value));

        if (messageReadModel == null ||
            messageReadModel.ToPeerId != MyTelegramConsts.RepliesServiceUserId ||
            messageReadModel.ToPeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var blockedUserId = messageReadModel!.FwdHeader?.FromId?.PeerId ?? 0;
        if (blockedUserId <= 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        await repliesBlockService.BlockAsync(input.UserId, blockedUserId, obj.ReportSpam);

        var messageIds = new List<int>();
        if (obj.DeleteHistory)
        {
            messageIds.AddRange(await GetMirroredMessageIdsAsync(input.UserId, blockedUserId));
        }
        else if (obj.DeleteMessage)
        {
            messageIds.Add(obj.MsgId);
        }

        if (messageIds.Count == 0)
        {
            return new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>(),
                Date = CurrentDate
            };
        }

        var messageItemsToBeDeletedList =
            await queryProcessor.ProcessAsync(new GetMessageItemListToBeDeletedQuery(input.UserId, messageIds, false));
        var command = new StartDeleteMessagesCommand(TempId.New, input.ToRequestInfo(), messageItemsToBeDeletedList,
            false, false, null, null);
        await commandBus.PublishAsync(command);

        // The deletion itself is delivered by the delete-messages saga as updateDeleteMessages; the
        // reply to this call only has to acknowledge it.
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }

    /// <summary>
    /// Ids of every message the blocked user's replies produced in the caller's @replies chat.
    /// </summary>
    private async Task<List<int>> GetMirroredMessageIdsAsync(long userId, long blockedUserId)
    {
        var builder = Builders<BsonDocument>.Filter;
        var filter = builder.And(
            builder.Eq("OwnerPeerId", userId),
            builder.Eq("ToPeerId", MyTelegramConsts.RepliesServiceUserId),
            builder.Eq("FwdHeader.FromId.PeerId", blockedUserId));

        var documents = await mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("MessageId"))
            .ToListAsync();

        return documents
            .Select(p => p.GetValue("MessageId", 0).ToInt32())
            .Where(p => p > 0)
            .ToList();
    }
}
