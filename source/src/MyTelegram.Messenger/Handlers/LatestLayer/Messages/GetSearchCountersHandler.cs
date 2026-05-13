using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get the number of results that would be found by a <a href="https://corefork.telegram.org/method/messages.search">messages.search</a> call with the same parameters
/// </summary>
internal sealed class GetSearchCountersHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSearchCounters, TVector<MyTelegram.Schema.Messages.ISearchCounter>>
{
    protected override async Task<TVector<ISearchCounter>> HandleCoreAsync(IRequestInput input, RequestGetSearchCounters obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        await accessHashHelper.CheckAccessHashAsync(input, obj.SavedPeerId);
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var result = new List<ISearchCounter>();
        foreach (var filter in obj.Filters)
        {
            var filterDef = MessageSearchMongoHelper.BuildFilter(input, peerHelper, obj.Peer, obj.SavedPeerId, obj.TopMsgId, filter);
            var count = await collection.CountDocumentsAsync(filterDef);
            result.Add(new TSearchCounter { Count = (int)count, Filter = filter });
        }

        return new TVector<ISearchCounter>(result);
    }
}
