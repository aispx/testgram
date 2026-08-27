using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetSavedDialogsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IGetHistoryConverterService getHistoryConverterService,
    IChannelAppService channelAppService,
    IDraftConverterService draftConverterService,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedDialogs, MyTelegram.Schema.Messages.ISavedDialogs>

{
    protected override async Task<MyTelegram.Schema.Messages.ISavedDialogs> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedDialogs obj)
    {
        if (obj.ParentPeer != null)
        {
            var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
            var monoforumReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
            if (monoforumReadModel == null || !monoforumReadModel.IsMonoforum)
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

            // Every monoforum topic is a private conversation between one user and the channel. Only a
            // manager of the broadcast side may list them all; anyone else sees just their own topic,
            // otherwise this enumerates every DM in the monoforum to any caller.
            var canManage = await MonoforumAccessHelper.CanManageAllTopicsAsync(
                monoforumReadModel!, input.UserId, channelAppService);
            var scopeToSelf = canManage ? null : new Peer(PeerType.User, input.UserId);

            var limit = obj.Limit > 0 ? obj.Limit : 20;
            var historyLimit = Math.Clamp(limit * 50, limit, 2000);
            var history = await messageAppService.GetHistoryAsync(new GetHistoryInput
            {
                SavedPeerId = scopeToSelf,
                OwnerPeerId = monoforumPeer.PeerId,
                SelfUserId = input.UserId,
                AddOffset = 0,
                Limit = historyLimit,
                MaxId = obj.OffsetId,
                OffsetId = obj.OffsetId,
                Peer = monoforumPeer
            });

            var topMessages = history.MessageList
                .Where(m => m.SavedPeerId != null)
                .GroupBy(m => $"{m.SavedPeerId!.PeerType}:{m.SavedPeerId.PeerId}")
                .Select(g => g.OrderByDescending(m => m.MessageId).First())
                .OrderByDescending(m => m.MessageId)
                .ToList();

            var totalCount = topMessages.Count;
            var page = topMessages.Take(limit).ToList();
            if (obj.Hash != 0 && obj.Hash == CalculateHash(page))
            {
                return new TSavedDialogsNotModified { Count = totalCount };
            }

            history.MessageList = page;
            var converted = getHistoryConverterService.ToMessages(input, history, input.Layer);
            var (messageList, chatList, userList) = ExtractMessages(converted);
            var stateMap = await MonoForumTopicStateHelper.GetStatesAsync(
                mongoDatabase,
                monoforumPeer.PeerId,
                input.UserId,
                page.Select(p => p.SavedPeerId!));
            var messageCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");

            // A monoforum topic keeps its own draft, keyed by the monoforum_peer_id the client sends in
            // messages.saveDraft. See https://corefork.telegram.org/api/drafts
            var topicDrafts = await MonoForumDraftHelper.GetTopicDraftsAsync(queryProcessor,
                draftConverterService, input.UserId, monoforumPeer.PeerId, input.Layer);

            var monoDialogs = new List<ISavedDialog>();
            foreach (var m in page)
            {
                var state = stateMap.GetValueOrDefault(MonoForumTopicStateHelper.BuildId(monoforumPeer.PeerId, input.UserId, m.SavedPeerId!));
                var readInboxMaxId = MonoForumTopicStateHelper.GetReadInboxMaxId(state);
                var unreadCount = (int)await messageCollection.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerPeerId", monoforumPeer.PeerId),
                        Builders<BsonDocument>.Filter.Eq("SavedPeerId.PeerType", m.SavedPeerId!.PeerType.ToString()),
                        Builders<BsonDocument>.Filter.Eq("SavedPeerId.PeerId", m.SavedPeerId.PeerId),
                        Builders<BsonDocument>.Filter.Gt("MessageId", readInboxMaxId),
                        Builders<BsonDocument>.Filter.Ne("SenderUserId", input.UserId)));

                monoDialogs.Add(new TMonoForumDialog
                {
                    Peer = ToSchemaPeer(m.SavedPeerId!),
                    TopMessage = m.MessageId,
                    ReadInboxMaxId = readInboxMaxId,
                    ReadOutboxMaxId = MonoForumTopicStateHelper.GetReadOutboxMaxId(state),
                    UnreadCount = unreadCount,
                    UnreadReactionsCount = 0,
                    UnreadMark = MonoForumTopicStateHelper.GetUnreadMark(state),
                    Draft = topicDrafts.GetValueOrDefault(m.SavedPeerId!.PeerId)
                });
            }

            if (totalCount > page.Count)
            {
                return new TSavedDialogsSlice
                {
                    Count = totalCount,
                    Dialogs = [.. monoDialogs],
                    Messages = [.. messageList],
                    Chats = [.. chatList],
                    Users = [.. userList]
                };
            }

            return new TSavedDialogs
            {
                Dialogs = [.. monoDialogs],
                Messages = [.. messageList],
                Chats = [.. chatList],
                Users = [.. userList]
            };
        }

        return new TSavedDialogs { Chats = new TVector<IChat>(), Dialogs = [], Messages = new TVector<IMessage>(), Users = new TVector<IUser>() };
    }

    internal static (List<IMessage> Messages, List<IChat> Chats, List<IUser> Users) ExtractMessages(MyTelegram.Schema.Messages.IMessages converted)
    {
        return converted switch
        {
            TMessages tm => (tm.Messages.ToList(), tm.Chats.ToList(), tm.Users.ToList()),
            TChannelMessages tcm => (tcm.Messages.ToList(), tcm.Chats.ToList(), tcm.Users.ToList()),
            _ => ([], [], [])
        };
    }

    internal static IPeer ToSchemaPeer(Peer peer)
    {
        return peer.PeerType switch
        {
            PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
            _ => new TPeerUser { UserId = peer.PeerId }
        };
    }

    internal static long CalculateHash(IReadOnlyCollection<IMessageReadModel> topMessages)
    {
        long hash = 0;
        foreach (var message in topMessages)
        {
            if (message.SavedPeerId == null)
            {
                return 0;
            }

            hash = MessageSearchMongoHelper.CalcHash(hash, message.Pinned ? 1 : 0);
            hash = MessageSearchMongoHelper.CalcHash(hash, Math.Abs(message.SavedPeerId.PeerId));
            hash = MessageSearchMongoHelper.CalcHash(hash, message.MessageId);
            hash = MessageSearchMongoHelper.CalcHash(hash, message.EditDate ?? message.Date);
        }

        return hash;
    }
}
