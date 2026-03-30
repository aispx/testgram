using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class RequestCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<RequestRequestCall, MyTelegram.Schema.Phone.IPhoneCall>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.Phone.IPhoneCall> HandleCoreAsync(IRequestInput input, RequestRequestCall obj)
    {
        long calleeId;
        if (obj.UserId is TInputUser inputUser)
        {
            calleeId = inputUser.UserId;
        }
        else if (obj.UserId is TInputUserSelf)
        {
            calleeId = input.UserId;
        }
        else
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        if (calleeId == input.UserId)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var callId = Random.Shared.NextInt64();
        callId = Math.Abs(callId);
        if (callId == 0) callId = 1;

        var accessHash = Random.Shared.NextInt64();
        accessHash = Math.Abs(accessHash);
        if (accessHash == 0) accessHash = 1;

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var session = new CallSessionDocument
        {
            Id = callId,
            CallId = callId,
            AccessHash = accessHash,
            CallerId = input.UserId,
            CalleeId = calleeId,
            RandomId = obj.RandomId,
            GAHash = obj.GAHash,
            Video = obj.Video,
            State = "requested",
            Date = currentDate,
            ProtocolJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                UdpP2p = obj.Protocol is TPhoneCallProtocol p ? p.UdpP2p : true,
                UdpReflector = obj.Protocol is TPhoneCallProtocol p2 ? p2.UdpReflector : true,
                MinLayer = obj.Protocol is TPhoneCallProtocol p3 ? p3.MinLayer : 65,
                MaxLayer = obj.Protocol is TPhoneCallProtocol p4 ? p4.MaxLayer : 92
            })
        };
        await _callCollection.InsertOneAsync(session);

        var phoneCallRequested = new MyTelegram.Schema.TPhoneCallRequested
        {
            Id = callId,
            AccessHash = accessHash,
            AdminId = input.UserId,
            ParticipantId = calleeId,
            GAHash = obj.GAHash,
            Date = currentDate,
            Protocol = BuildProtocol(obj.Protocol),
            Video = obj.Video
        };

        var users = await userConverterService.GetUserListAsync(input, new List<long> { input.UserId, calleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallRequested };

        var calleePeer = new Peer(PeerType.User, calleeId);
        await objectMessageSender.PushMessageToPeerAsync(calleePeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { updatePhoneCall },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallRequested,
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
}
