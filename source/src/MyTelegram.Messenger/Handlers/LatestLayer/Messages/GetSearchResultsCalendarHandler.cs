using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns information about the next messages of the specified type in the chat split by days.
/// </summary>
internal sealed class GetSearchResultsCalendarHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSearchResultsCalendar, MyTelegram.Schema.Messages.ISearchResultsCalendar>
{
    private const int SecondsPerDay = 86400;
    private const int MessagePageSize = 100;
    protected override async Task<MyTelegram.Schema.Messages.ISearchResultsCalendar> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSearchResultsCalendar obj)
    {
        // inputMessagesFilterEmpty and inputMessagesFilterMyMentions are not supported here.
        // See https://corefork.telegram.org/method/messages.getSearchResultsCalendar
        if (!MessageFilterHelper.IsSupportedByPositionsAndCalendar(obj.Filter))
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var filter = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId, null, obj.Filter, obj.OffsetId, obj.OffsetDate);
        var count = (int)await collection.CountDocumentsAsync(filter);

        // Group every matching message by day server-side: the calendar must cover the whole
        // history, so it cannot be built from a single truncated page of messages.
        var renderedMatch = filter.Render(
            new RenderArgs<BsonDocument>(
                BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
                BsonSerializer.SerializerRegistry));

        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
        {
            new("$match", renderedMatch),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument("$subtract", new BsonArray { "$Date", new BsonDocument("$mod", new BsonArray { "$Date", SecondsPerDay }) }) },
                { "MinMsgId", new BsonDocument("$min", "$MessageId") },
                { "MaxMsgId", new BsonDocument("$max", "$MessageId") },
                { "MinDate", new BsonDocument("$min", "$Date") },
                { "Count", new BsonDocument("$sum", 1) }
            }),
            new("$sort", new BsonDocument("_id", -1))
        };

        var periodDocs = await collection.Aggregate(pipeline).ToListAsync();

        var periods = periodDocs
            .Select(d => (ISearchResultsCalendarPeriod)new TSearchResultsCalendarPeriod
            {
                Date = d["_id"].AsInt32,
                MinMsgId = d["MinMsgId"].AsInt32,
                MaxMsgId = d["MaxMsgId"].AsInt32,
                Count = d["Count"].AsInt32
            })
            .ToList();

        // offset_id_offset is the absolute index of offset_id within the filtered history.
        var offsetIdOffset = 0;
        if (obj.OffsetId > 0)
        {
            var newerFilter = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId,
                null, obj.Filter);
            newerFilter &= Builders<BsonDocument>.Filter.Gt("MessageId", obj.OffsetId);
            offsetIdOffset = (int)await collection.CountDocumentsAsync(newerFilter);
        }

        var (peer, savedPeer, ownerPeerId) = MessageSearchMongoHelper.ResolveScope(peerHelper, input, obj.Peer, obj.SavedPeerId);
        var searchOutput = await messageAppService.SearchAsync(new SearchInput
        {
            OwnerPeerId = ownerPeerId,
            SelfUserId = input.UserId,
            Limit = MessagePageSize,
            Q = string.Empty,
            OffsetId = obj.OffsetId,
            Peer = peer,
            MaxDate = obj.OffsetDate,
            MessageType = MessageFilterHelper.IsPinnedFilter(obj.Filter) ? MessageType.Pinned : MessageType.Unknown,
            MessageTypes = MessageFilterHelper.GetMessageTypes(obj.Filter),
            SavedPeerId = savedPeer
        });
        var converted = getHistoryConverterService.ToMessages(input, searchOutput, input.Layer);
        var (messages, chats, users) = GetSavedDialogsHandler.ExtractMessages(converted);

        return new TSearchResultsCalendar
        {
            Inexact = false,
            Count = count,
            MinDate = periodDocs.Count > 0 ? periodDocs.Min(d => d["MinDate"].AsInt32) : 0,
            MinMsgId = periodDocs.Count > 0 ? periodDocs.Min(d => d["MinMsgId"].AsInt32) : 0,
            OffsetIdOffset = offsetIdOffset,
            Periods = new TVector<ISearchResultsCalendarPeriod>(periods),
            Messages = [.. messages],
            Chats = [.. chats],
            Users = [.. users]
        };
    }
}
