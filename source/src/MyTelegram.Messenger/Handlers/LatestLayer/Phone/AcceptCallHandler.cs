using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class AcceptCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    IAccessHashHelper2 accessHashHelper2)
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

        var filter = Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id);

        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null ||
            (!session.HasAccessHashForUser(input.UserId, inputPhoneCall.AccessHash) &&
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)))
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        ValidateHandshakeProtocol(obj.Protocol);

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
            .Set(s => s.CalleeLibraryVersions, [.. PhoneCallProtocolHelper.GetLibraryVersions(obj.Protocol)])
            .Set(s => s.State, "accepted");

        await _callCollection.UpdateOneAsync(filter, update);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var phoneCallAcceptedForCaller = CreatePhoneCallAccepted(
            session,
            session.GetAccessHashForUser(session.CallerId),
            obj.GB,
            obj.Protocol,
            currentDate);
        var phoneCallWaitingForCallee = CreatePhoneCallWaiting(
            session,
            session.GetAccessHashForUser(session.CalleeId),
            obj.Protocol,
            currentDate);

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallAcceptedForCaller };

        var callerPeer = new Peer(PeerType.User, session.CallerId);
        await objectMessageSender.PushMessageToPeerAsync(callerPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { updatePhoneCall },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        await SendCallAcceptedServiceMessageAsync(input, session.CallId, session.CallerId, session.Video);

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallWaitingForCallee,
            Users = usersVector
        };
    }

    private static Schema.TPhoneCallAccepted CreatePhoneCallAccepted(
        CallSessionDocument session,
        long accessHash,
        byte[] gb,
        IPhoneCallProtocol? protocol,
        int currentDate)
    {
        return new Schema.TPhoneCallAccepted
        {
            Id = session.CallId,
            AccessHash = accessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            GB = gb,
            Protocol = PhoneCallProtocolHelper.Negotiate(session.CallerLibraryVersions, protocol),
            Date = currentDate,
            Video = session.Video
        };
    }

    private static Schema.TPhoneCallWaiting CreatePhoneCallWaiting(
        CallSessionDocument session,
        long accessHash,
        IPhoneCallProtocol? protocol,
        int currentDate)
    {
        return new Schema.TPhoneCallWaiting
        {
            Id = session.CallId,
            AccessHash = accessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            Protocol = PhoneCallProtocolHelper.Negotiate(session.CallerLibraryVersions, protocol),
            Date = currentDate,
            Video = session.Video
        };
    }

    private static void ValidateHandshakeProtocol(IPhoneCallProtocol? protocol)
    {
        if (!PhoneCallProtocolHelper.HasValidLegacyFlags(protocol))
        {
            RpcErrors.RpcErrors400.CallProtocolFlagsInvalid.ThrowRpcError();
        }

        if (!PhoneCallProtocolHelper.HasValidLegacyLayers(protocol))
        {
            RpcErrors.RpcErrors400.CallProtocolLayerInvalid.ThrowRpcError();
        }
    }

    private async Task SendCallAcceptedServiceMessageAsync(IRequestInput input, long callId, long callerId, bool video)
    {
        var action = new TMessageActionPhoneCall
        {
            CallId = callId,
            Video = video
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
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
