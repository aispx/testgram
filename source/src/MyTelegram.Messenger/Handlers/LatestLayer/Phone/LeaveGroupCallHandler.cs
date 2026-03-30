using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class LeaveGroupCallHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestLeaveGroupCall, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestLeaveGroupCall obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, inputGroupCall.Id),
            Builders<GroupCallDocument>.Filter.Eq(g => g.AccessHash, inputGroupCall.AccessHash)
        );

        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var updateFilter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, inputGroupCall.Id),
            Builders<GroupCallDocument>.Filter.Eq(g => g.AccessHash, inputGroupCall.AccessHash)
        );

        await _groupCallCollection.UpdateOneAsync(updateFilter,
            Builders<GroupCallDocument>.Update
                .PullFilter(g => g.Participants, p => p.PeerId == input.UserId && p.Source == obj.Source)
                .Inc(g => g.Version, 1));

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = currentDate
        };
    }
}
