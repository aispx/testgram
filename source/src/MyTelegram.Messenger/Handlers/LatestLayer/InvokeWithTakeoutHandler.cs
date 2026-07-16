using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Invoke a method within a <a href="https://corefork.telegram.org/api/takeout">takeout session, see here » for more info</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithTakeout"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithTakeoutHandler(
    IHandlerHelper handlerHelper,
    IMongoDatabase mongoDatabase)
    : BaseObjectHandler<MyTelegram.Schema.RequestInvokeWithTakeout, IObject>
{
    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInvokeWithTakeout obj)
    {
        var session = await mongoDatabase.GetCollection<BsonDocument>("takeout_sessions")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", obj.TakeoutId),
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("Active", true),
                Builders<BsonDocument>.Filter.Gt("ExpiresAt", DateTime.UtcNow)))
            .FirstOrDefaultAsync();
        if (session == null)
        {
            RpcErrors.RpcErrors400.TakeoutInvalid.ThrowRpcError();
        }

        using (TakeoutContext.Enter(new TakeoutSessionScope(
            obj.TakeoutId,
            session.GetValue("Contacts", false).ToBoolean())))
        {
            return await SubQueryExecutor.ExecuteInnerAsync(handlerHelper, input, obj.Query);
        }
    }
}
