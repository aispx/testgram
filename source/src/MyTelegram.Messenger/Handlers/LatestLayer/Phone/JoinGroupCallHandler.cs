using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class JoinGroupCallHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestJoinGroupCall, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestJoinGroupCall obj)
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
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ssrc = Random.Shared.Next(100000, 999999);

        var participant = new GroupCallParticipantDoc
        {
            PeerId = input.UserId,
            PeerType = 0,
            Source = ssrc,
            Muted = obj.Muted,
            VideoStopped = obj.VideoStopped,
            Date = currentDate,
            ParamsJson = obj.Params?.Data
        };

        var updateFilter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, inputGroupCall.Id),
            Builders<GroupCallDocument>.Filter.Eq(g => g.AccessHash, inputGroupCall.AccessHash)
        );

        await _groupCallCollection.UpdateOneAsync(updateFilter,
            Builders<GroupCallDocument>.Update
                .Push(g => g.Participants, participant)
                .Inc(g => g.Version, 1));

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = currentDate
        };
    }
}
