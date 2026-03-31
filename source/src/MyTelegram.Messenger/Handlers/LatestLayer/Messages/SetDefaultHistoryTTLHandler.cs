using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

public class SetDefaultHistoryTTLHandler : RpcResultObjectHandler<RequestSetDefaultHistoryTTL, IBool>
{
    private readonly IMongoDatabase _mongoDatabase;

    public SetDefaultHistoryTTLHandler(IMongoDatabase mongoDatabase)
    {
        _mongoDatabase = mongoDatabase;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestSetDefaultHistoryTTL obj)
    {
        var period = obj.Period;
        if (period < 0 || period > 31536000)
        {
            RpcErrors.RpcErrors400.TtlPeriodInvalid.ThrowRpcError();
        }

        var userCollection = _mongoDatabase.GetCollection<UserReadModel>("users");
        var filter = Builders<UserReadModel>.Filter.Eq(u => u.UserId, input.UserId);
        var update = Builders<UserReadModel>.Update.Set(u => u.DefaultHistoryTTL, period);
        await userCollection.UpdateOneAsync(filter, update);

        return new TBoolTrue();
    }
}