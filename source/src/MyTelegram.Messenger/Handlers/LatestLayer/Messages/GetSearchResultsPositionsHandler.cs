using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns sparse positions of messages of the specified type in the chat.
/// </summary>
internal sealed class GetSearchResultsPositionsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSearchResultsPositions, MyTelegram.Schema.Messages.ISearchResultsPositions>
{
    protected override async Task<MyTelegram.Schema.Messages.ISearchResultsPositions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSearchResultsPositions obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        await accessHashHelper.CheckAccessHashAsync(input, obj.SavedPeerId);
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var filter = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId, null, obj.Filter, obj.OffsetId);
        var count = (int)await collection.CountDocumentsAsync(filter);
        var limit = obj.Limit > 0 ? obj.Limit : 20;
        var docs = await collection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("MessageId"))
            .Project(Builders<BsonDocument>.Projection.Include("MessageId").Include("Date"))
            .Limit(limit)
            .ToListAsync();

        return new TSearchResultsPositions
        {
            Count = count,
            Positions = new TVector<ISearchResultsPosition>(docs.Select((d, i) => (ISearchResultsPosition)new TSearchResultPosition
            {
                MsgId = d["MessageId"].AsInt32,
                Date = d["Date"].AsInt32,
                Offset = i
            }))
        };
    }
}
