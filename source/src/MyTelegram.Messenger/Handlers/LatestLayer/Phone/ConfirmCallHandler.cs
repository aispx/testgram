using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class ConfirmCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IOptions<MyTelegramMessengerServerOptions> optionsAccessor,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestConfirmCall, MyTelegram.Schema.Phone.IPhoneCall>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.Phone.IPhoneCall> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestConfirmCall obj)
    {
        if (obj.Peer is not TInputPhoneCall inputPhoneCall)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var filter = Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id);

        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null ||
            (session.AccessHash != inputPhoneCall.AccessHash &&
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)))
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.CallerId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (session.State == "discarded")
        {
            RpcErrors.RpcErrors400.CallAlreadyDeclined.ThrowRpcError();
            return null!;
        }

        if (session.State != "accepted")
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.GA, obj.GA)
            .Set(s => s.KeyFingerprint, obj.KeyFingerprint)
            .Set(s => s.State, "confirmed");

        await _callCollection.UpdateOneAsync(filter, update);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var connections = new TVector<MyTelegram.Schema.IPhoneConnection>();
        var webRtcConnections = optionsAccessor.Value.WebRtcConnections;

        if (webRtcConnections != null && webRtcConnections.Count > 0)
        {
            long connectionId = 1;
            foreach (var webRtcConfig in webRtcConnections)
            {
                if (webRtcConfig.Stun)
                {
                    connections.Add(new MyTelegram.Schema.TPhoneConnectionWebrtc
                    {
                        Id = connectionId++,
                        Ip = webRtcConfig.Ip,
                        Ipv6 = webRtcConfig.Ipv6 ?? "",
                        Port = webRtcConfig.Port,
                        Turn = false,
                        Stun = true,
                        Username = "",
                        Password = ""
                    });
                }

                if (webRtcConfig.Turn)
                {
                    connections.Add(new MyTelegram.Schema.TPhoneConnectionWebrtc
                    {
                        Id = connectionId++,
                        Ip = webRtcConfig.Ip,
                        Ipv6 = webRtcConfig.Ipv6 ?? "",
                        Port = webRtcConfig.Port,
                        Turn = true,
                        Stun = false,
                        Username = webRtcConfig.UserName,
                        Password = webRtcConfig.Password
                    });
                }
            }
        }

        if (connections.Count == 0)
        {
            throw new InvalidOperationException("WebRTC connections not configured. Please configure App__WebRtcConnections in .env file.");
        }

        var phoneCall = new Schema.TPhoneCall
        {
            Id = session.CallId,
            AccessHash = session.AccessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            GAOrB = obj.GA,
            KeyFingerprint = obj.KeyFingerprint,
            Protocol = PhoneCallProtocolHelper.Normalize(obj.Protocol),
            Date = session.Date,
            StartDate = currentDate,
            Connections = connections,
            Video = session.Video
        };

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCall };

        var otherUserId = input.UserId == session.CallerId ? session.CalleeId : session.CallerId;
        var otherPeer = new Peer(PeerType.User, otherUserId);
        await objectMessageSender.PushMessageToPeerAsync(otherPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { updatePhoneCall },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            });

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCall,
            Users = usersVector
        };
    }

}
