using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Terminate a <a href="https://corefork.telegram.org/api/takeout">takeout session, see here » for more info</a>.
/// Possible errors
/// Code Type Description
/// 403 TAKEOUT_REQUIRED A <a href="https://corefork.telegram.org/api/takeout">takeout</a> session needs to be initialized first, <a href="https://corefork.telegram.org/api/takeout">see here » for more info</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.finishTakeoutSession"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class FinishTakeoutSessionHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestFinishTakeoutSession, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestFinishTakeoutSession obj)
    {
        var session = TakeoutContext.CurrentSession;
        if (session == null)
        {
            RpcErrors.RpcErrors403.TakeoutRequired.ThrowRpcError();
        }

        await mongoDatabase.GetCollection<BsonDocument>("takeout_sessions").UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", session.TakeoutId),
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)),
            Builders<BsonDocument>.Update
                .Set("Active", false)
                .Set("Success", obj.Success)
                .Set("FinishedAt", DateTime.UtcNow));
        return new TBoolTrue();
    }
}
