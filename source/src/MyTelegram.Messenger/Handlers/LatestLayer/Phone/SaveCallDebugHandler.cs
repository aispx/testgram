using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class SaveCallDebugHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<RequestSaveCallDebug, IBool>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestSaveCallDebug obj)
    {
        if (obj.Peer is not TInputPhoneCall inputPhoneCall)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        var filter = Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id);

        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null ||
            (!session.HasAccessHashForUser(input.UserId, inputPhoneCall.AccessHash) &&
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)))
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        if ((session.CallerId != input.UserId && session.CalleeId != input.UserId) ||
            session.State != "discarded")
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.DebugJson, obj.Debug?.Data);

        await _callCollection.UpdateOneAsync(filter, update);

        return new TBoolTrue();
    }
}
