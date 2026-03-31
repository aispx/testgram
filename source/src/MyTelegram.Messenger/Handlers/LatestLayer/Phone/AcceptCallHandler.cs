using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class AcceptCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestAcceptCall, MyTelegram.Schema.Phone.IPhoneCall>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.Phone.IPhoneCall> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestAcceptCall obj)
    {
        if (obj.Peer is not TInputPhoneCall inputPhoneCall)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var filter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id),
            Builders<CallSessionDocument>.Filter.Eq(s => s.AccessHash, inputPhoneCall.AccessHash)
        );

        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.State == "accepted" || session.State == "confirmed")
        {
            RpcErrors.RpcErrors400.CallAlreadyAccepted.ThrowRpcError();
            return null!;
        }

        if (session.State == "discarded")
        {
            RpcErrors.RpcErrors400.CallAlreadyDeclined.ThrowRpcError();
            return null!;
        }

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.GB, obj.GB)
            .Set(s => s.State, "accepted");

        await _callCollection.UpdateOneAsync(filter, update);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var phoneCallAccepted = new Schema.TPhoneCallAccepted
        {
            Id = session.CallId,
            AccessHash = session.AccessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            GB = obj.GB,
            Protocol = BuildProtocol(obj.Protocol),
            Date = currentDate,
            Video = session.Video
        };

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallAccepted };

        var callerPeer = new Peer(PeerType.User, session.CallerId);
        await objectMessageSender.PushMessageToPeerAsync(callerPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { updatePhoneCall },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        await SendCallAcceptedServiceMessageAsync(input, session.CallId, session.CallerId, session.CalleeId);

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallAccepted,
            Users = usersVector
        };
    }

    private static TPhoneCallProtocol BuildProtocol(IPhoneCallProtocol? proto)
    {
        var p = proto as TPhoneCallProtocol;
        return new TPhoneCallProtocol
        {
            UdpP2p = p?.UdpP2p ?? true,
            UdpReflector = p?.UdpReflector ?? true,
            MinLayer = p?.MinLayer ?? 65,
            MaxLayer = p?.MaxLayer ?? 92,
            LibraryVersions = new TVector<string> { "2.7.7" }
        };
    }

    private async Task SendCallAcceptedServiceMessageAsync(IRequestInput input, long callId, long callerId, long calleeId)
    {
        var action = new TMessageActionPhoneCall
        {
            CallId = callId,
            Video = false
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            callerId,
            new Peer(PeerType.User, callerId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);
    }
}
