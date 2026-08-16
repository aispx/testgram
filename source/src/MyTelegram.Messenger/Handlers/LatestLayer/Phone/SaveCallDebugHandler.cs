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

    /// <summary>
    /// Upper bound on the stored libtgvoip debug blob. A discarded call's debug JSON is a few KB;
    /// without a cap a client could persist an arbitrarily large blob against a call it participated
    /// in, filling storage. 64 KB is far above any legitimate debug payload.
    /// </summary>
    private const int MaxDebugLength = 64 * 1024;

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
            session.State != CallSessionStates.Discarded)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        var debugData = obj.Debug?.Data;
        if (debugData is { Length: > MaxDebugLength })
        {
            debugData = debugData[..MaxDebugLength];
        }

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.DebugJson, debugData);

        await _callCollection.UpdateOneAsync(filter, update);

        return new TBoolTrue();
    }
}
