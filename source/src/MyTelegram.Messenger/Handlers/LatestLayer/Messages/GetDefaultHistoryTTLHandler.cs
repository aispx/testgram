using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using TDefaultHistoryTTL = MyTelegram.Schema.TDefaultHistoryTTL;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

public class GetDefaultHistoryTTLHandler : RpcResultObjectHandler<RequestGetDefaultHistoryTTL, IDefaultHistoryTTL>
{
    private readonly IMongoDatabase _mongoDatabase;

    public GetDefaultHistoryTTLHandler(IMongoDatabase mongoDatabase)
    {
        _mongoDatabase = mongoDatabase;
    }

    protected override async Task<IDefaultHistoryTTL> HandleCoreAsync(IRequestInput input, RequestGetDefaultHistoryTTL obj)
    {
        var userCollection = _mongoDatabase.GetCollection<UserReadModel>("users");
        var filter = Builders<UserReadModel>.Filter.Eq(u => u.UserId, input.UserId);
        var user = await userCollection.Find(filter).FirstOrDefaultAsync();

        var period = user?.DefaultHistoryTTL ?? 0;

        return new TDefaultHistoryTTL { Period = period };
    }
}