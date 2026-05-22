using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Optional: notify the server that the user is currently busy in a call: this will automatically refuse all incoming phone calls until the current phone call is ended.
/// Possible errors
/// Code Type Description
/// 400 CALL_ALREADY_DECLINED The call was already declined.
/// 400 CALL_PEER_INVALID The provided call peer object is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.receivedCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReceivedCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestReceivedCall, IBool>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestReceivedCall obj)
    {
        if (obj.Peer is not TInputPhoneCall inputPhoneCall)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        var filter = Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id);
        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null ||
            (session.AccessHash != inputPhoneCall.AccessHash &&
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)) ||
            session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return new TBoolTrue();
        }

        if (session.State == "discarded")
        {
            RpcErrors.RpcErrors400.CallAlreadyDeclined.ThrowRpcError();
            return new TBoolTrue();
        }

        if (session.State != "requested")
        {
            return new TBoolTrue();
        }

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _callCollection.UpdateOneAsync(filter,
            Builders<CallSessionDocument>.Update
                .Set(s => s.State, "received")
                .Set(s => s.ReceivedDate, currentDate));

        var waitingCall = new TPhoneCallWaiting
        {
            Id = session.CallId,
            AccessHash = session.AccessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            Date = session.Date,
            ReceiveDate = currentDate,
            Protocol = PhoneCallProtocolHelper.FromLibraryVersions(session.CallerLibraryVersions),
            Video = session.Video
        };
        var users = await userConverterService.GetUserListAsync(
            input,
            [session.CallerId, session.CalleeId],
            false,
            false,
            input.Layer);

        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, session.CallerId),
            new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = waitingCall } },
                Users = new TVector<IUser>(users),
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        return new TBoolTrue();
    }
}
