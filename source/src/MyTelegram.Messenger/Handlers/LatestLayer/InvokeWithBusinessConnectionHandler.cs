using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Invoke a method using a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">Telegram Business Bot connection, see here » for more info, including a list of the methods that can be wrapped in this constructor</a>.Make sure to always send queries wrapped in a <code>invokeWithBusinessConnection</code> to the datacenter ID, specified in the <code>dc_id</code> field of the <a href="https://corefork.telegram.org/constructor/botBusinessConnection">botBusinessConnection</a> that is being used.
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithBusinessConnection"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithBusinessConnectionHandler : RpcResultObjectHandler<MyTelegram.Schema.RequestInvokeWithBusinessConnection, IObject>
{
    private readonly IMongoDatabase _database;
    private readonly IHandlerHelper _handlerHelper;

    public InvokeWithBusinessConnectionHandler(IMongoDatabase database, IHandlerHelper handlerHelper)
    {
        _database = database;
        _handlerHelper = handlerHelper;
    }

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.RequestInvokeWithBusinessConnection obj)
    {
        var botId = input.UserId;
        var connectionId = obj.ConnectionId;

        // Verify business connection exists and bot has access
        var collection = _database.GetCollection<BsonDocument>("connected_business_bots");
        var filter = Builders<BsonDocument>.Filter.Eq("ConnectionId", connectionId);
        var connection = await collection.Find(filter).FirstOrDefaultAsync();

        if (connection == null)
        {
            RpcErrors.RpcErrors400.BusinessConnectionInvalid.ThrowRpcError();
        }

        var connBotId = connection["BotId"].AsInt64;
        if (connBotId != botId)
        {
            RpcErrors.RpcErrors400.BusinessConnectionInvalid.ThrowRpcError();
        }

        var userId = connection["UserId"].AsInt64;

        // The inner request runs under the connected user's identity, so the set of wrappable methods
        // has to be closed and each one gated on its own right. Checking only "Reply" and then
        // dispatching any constructor let a bot with the minimum grant reach account.*, auth.logOut and
        // every dialog's history as the user - a full account takeover.
        var rightsDoc = connection.TryGetValue("Rights", out var rightsValue) && rightsValue.IsBsonDocument
            ? rightsValue.AsBsonDocument
            : null;

        if (!BusinessConnectionRightsMap.IsWrappable(obj.Query.ConstructorId))
        {
            RpcErrors.RpcErrors400.BusinessConnectionNotAllowed.ThrowRpcError();
        }

        if (!BusinessConnectionRightsMap.IsAllowed(obj.Query.ConstructorId, rightsDoc))
        {
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }

        // Create new RequestInput with business user's ID.
        // Use a null-safe path: when input is already a RequestInput reuse the record 'with'
        // syntax; otherwise build a fresh RequestInput copied from the IRequestInput members so
        // there is no NullReferenceException when input is not a RequestInput.
        var businessInput = input is RequestInput ri
            ? ri with { UserId = userId }
            : new RequestInput(
                input.ConnectionId,
                input.ConnectionType,
                input.RequestId,
                input.ObjectId,
                input.ReqMsgId,
                input.SeqNumber,
                userId,
                input.AuthKeyId,
                input.PermAuthKeyId,
                input.Layer,
                input.Date,
                input.DeviceType,
                input.ClientIp,
                input.SessionId,
                input.AccessHashKeyId);

        // Execute the wrapped query as the business user. Unresolved inner constructors surface
        // 400 INPUT_CONSTRUCTOR_INVALID instead of throwing NotImplementedException.
        return await SubQueryExecutor.ExecuteInnerAsync(_handlerHelper, businessInput, obj.Query);
    }
}