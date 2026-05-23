using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class SendSignalingDataHandler(
    IMongoDatabase mongoDatabase,
    IObjectMessageSender objectMessageSender,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<RequestSendSignalingData, IBool>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestSendSignalingData obj)
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

        if (session.CallerId != input.UserId && session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        if (session.State == "discarded")
        {
            return new TBoolTrue();
        }

        if (session.State is not ("accepted" or "confirmed"))
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        var otherUserId = input.UserId == session.CallerId ? session.CalleeId : session.CallerId;
        var otherPeer = new Peer(PeerType.User, otherUserId);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signalingDataUpdate = new MyTelegram.Schema.TUpdatePhoneCallSignalingData
        {
            PhoneCallId = session.CallId,
            Data = obj.Data
        };

        await objectMessageSender.PushMessageToPeerAsync(otherPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { signalingDataUpdate },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        return new TBoolTrue();
    }
}
