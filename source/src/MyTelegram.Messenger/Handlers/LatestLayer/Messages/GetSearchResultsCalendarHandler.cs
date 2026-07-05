using MongoDB.Bson;
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
    protected override async Task<MyTelegram.Schema.Messages.ISearchResultsCalendar> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSearchResultsCalendar obj)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var filter = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId, null, obj.Filter, obj.OffsetId, obj.OffsetDate);
        var count = (int)await collection.CountDocumentsAsync(filter);
        var docs = await collection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("MessageId"))
            .Project(Builders<BsonDocument>.Projection.Include("MessageId").Include("Date"))
            .Limit(100)
            .ToListAsync();

        var periods = docs
            .GroupBy(d => DateTimeOffset.FromUnixTimeSeconds(d["Date"].AsInt32).UtcDateTime.Date)
            .Select(g => (ISearchResultsCalendarPeriod)new TSearchResultsCalendarPeriod
            {
                Date = (int)new DateTimeOffset(g.Key, TimeSpan.Zero).ToUnixTimeSeconds(),
                MinMsgId = g.Min(x => x["MessageId"].AsInt32),
                MaxMsgId = g.Max(x => x["MessageId"].AsInt32),
                Count = g.Count()
            })
            .OrderByDescending(x => ((TSearchResultsCalendarPeriod)x).Date)
            .ToList();

        var (peer, savedPeer, ownerPeerId) = MessageSearchMongoHelper.ResolveScope(peerHelper, input, obj.Peer, obj.SavedPeerId);
        var searchOutput = await messageAppService.SearchAsync(new SearchInput
        {
            OwnerPeerId = ownerPeerId,
            SelfUserId = input.UserId,
            Limit = Math.Max(1, Math.Min(100, docs.Count)),
            Q = string.Empty,
            OffsetId = obj.OffsetId,
            Peer = peer,
            MaxDate = obj.OffsetDate,
            MessageType = MessageSearchMongoHelper.GetMessageType(obj.Filter),
            SavedPeerId = savedPeer
        });
        var converted = getHistoryConverterService.ToMessages(input, searchOutput, input.Layer);
        var (messages, chats, users) = GetSavedDialogsHandler.ExtractMessages(converted);

        return new TSearchResultsCalendar
        {
            Inexact = count > docs.Count,
            Count = count,
            MinDate = docs.Count > 0 ? docs.Min(d => d["Date"].AsInt32) : 0,
            MinMsgId = docs.Count > 0 ? docs.Min(d => d["MessageId"].AsInt32) : 0,
            OffsetIdOffset = docs.Count > 0 ? 0 : null,
            Periods = new TVector<ISearchResultsCalendarPeriod>(periods),
            Messages = [.. messages],
            Chats = [.. chats],
            Users = [.. users]
        };
    }
}
