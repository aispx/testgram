using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class SendGroupCallMessageHandler(
    IMongoDatabase mongoDatabase,
    IIdGenerator idGenerator)
    : RpcResultObjectHandler<RequestSendGroupCallMessage, IUpdates>
{
    private readonly IMongoCollection<BsonDocument> _groupCallCollection =
        mongoDatabase.GetCollection<BsonDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendGroupCallMessage obj)
    {
        var call = obj.Call;
        if (call is not TInputGroupCall inputGroupCall)
        {
            throw new RpcException(RpcErrors.RpcErrors400.GroupcallInvalid);
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", inputGroupCall.Id),
            Builders<BsonDocument>.Filter.Eq("access_hash", inputGroupCall.AccessHash)
        );

        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            throw new RpcException(RpcErrors.RpcErrors400.GroupcallInvalid);
        }

        var peerId = groupCall.GetValue("peer_id", 0L).AsInt64;
        var peerType = groupCall.GetValue("peer_type", 0).AsInt32;

        var messageId = await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var fromId = new TPeerUser { UserId = input.UserId };

        var groupCallMessage = new TGroupCallMessage
        {
            Id = (int)messageId,
            FromId = fromId,
            Date = currentDate,
            Message = obj.Message
        };

        var outputGroupCall = new TInputGroupCall
        {
            Id = inputGroupCall.Id,
            AccessHash = inputGroupCall.AccessHash
        };

        var update = new TUpdateGroupCallMessage
        {
            Call = outputGroupCall,
            Message = groupCallMessage
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { update },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = currentDate
        };
    }
}