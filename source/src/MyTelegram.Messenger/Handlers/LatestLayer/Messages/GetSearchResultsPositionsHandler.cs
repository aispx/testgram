using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns sparse positions of messages of the specified type in the chat.
/// </summary>
internal sealed class GetSearchResultsPositionsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSearchResultsPositions, MyTelegram.Schema.Messages.ISearchResultsPositions>
{
    protected override async Task<MyTelegram.Schema.Messages.ISearchResultsPositions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSearchResultsPositions obj)
    {
        // inputMessagesFilterEmpty and inputMessagesFilterMyMentions are not supported here.
        // See https://corefork.telegram.org/method/messages.getSearchResultsPositions
        if (!MessageFilterHelper.IsSupportedByPositionsAndCalendar(obj.Filter))
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var filter = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId, null, obj.Filter, obj.OffsetId);
        var count = (int)await collection.CountDocumentsAsync(filter);
        var limit = obj.Limit > 0 ? obj.Limit : 20;
        var docs = await collection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("MessageId"))
            .Project(Builders<BsonDocument>.Projection.Include("MessageId").Include("Date"))
            .Limit(limit)
            .ToListAsync();

        // offset is the absolute index of the message within the whole filtered history, so paging
        // has to account for the messages newer than offset_id that were skipped by the filter.
        var skipped = 0;
        if (obj.OffsetId > 0)
        {
            var newerFilter = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId,
                null, obj.Filter);
            newerFilter &= Builders<BsonDocument>.Filter.Gte("MessageId", obj.OffsetId);
            skipped = (int)await collection.CountDocumentsAsync(newerFilter);
        }

        return new TSearchResultsPositions
        {
            Count = count + skipped,
            Positions = new TVector<ISearchResultsPosition>(docs.Select((d, i) => (ISearchResultsPosition)new TSearchResultPosition
            {
                MsgId = d["MessageId"].AsInt32,
                Date = d["Date"].AsInt32,
                Offset = skipped + i
            }))
        };
    }
}
