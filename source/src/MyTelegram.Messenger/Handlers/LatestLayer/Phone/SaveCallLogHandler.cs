using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Save phone call debug information
/// Possible errors
/// Code Type Description
/// 400 CALL_PEER_INVALID The provided call peer object is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.saveCallLog"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveCallLogHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestSaveCallLog, IBool>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestSaveCallLog obj)
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
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)) ||
            (session.CallerId != input.UserId && session.CalleeId != input.UserId) ||
            session.State != "discarded")
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        var update = obj.File switch
        {
            TInputFile file => Builders<CallSessionDocument>.Update
                .Set(s => s.LogFileId, file.Id)
                .Set(s => s.LogFileParts, file.Parts)
                .Set(s => s.LogFileName, file.Name)
                .Set(s => s.LogFileMd5Checksum, file.Md5Checksum),
            TInputFileBig file => Builders<CallSessionDocument>.Update
                .Set(s => s.LogFileId, file.Id)
                .Set(s => s.LogFileParts, file.Parts)
                .Set(s => s.LogFileName, file.Name)
                .Unset(s => s.LogFileMd5Checksum),
            _ => null
        };

        if (update == null)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        await _callCollection.UpdateOneAsync(filter, update);

        return new TBoolTrue();
    }
}
