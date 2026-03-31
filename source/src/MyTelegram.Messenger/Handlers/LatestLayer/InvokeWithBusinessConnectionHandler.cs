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

        // Check rights - verify bot has permission to reply
        var rightsDoc = connection["Rights"].AsBsonDocument;
        var canReply = rightsDoc.Contains("Reply") && rightsDoc["Reply"].AsBoolean;

        if (!canReply)
        {
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }

        // Create new RequestInput with business user's ID
        // Cast to RequestInput to use record 'with' syntax
        var businessInput = (input as RequestInput) with { UserId = userId };

        // Execute the wrapped query as the business user
        if (_handlerHelper.TryGetHandler(obj.Query.ConstructorId, out var handler))
        {
            return await handler.HandleAsync(businessInput, obj.Query)!;
        }

        throw new NotImplementedException($"Handler not found for constructor {obj.Query.ConstructorId}");
    }
}