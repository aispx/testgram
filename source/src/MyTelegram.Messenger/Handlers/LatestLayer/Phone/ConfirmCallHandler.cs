using MongoDB.Driver;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Services.Interfaces;
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
    IAccessHashHelper2 accessHashHelper2,
    IPrivacyAppService privacyAppService)
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
            (!session.HasAccessHashForUser(input.UserId, inputPhoneCall.AccessHash) &&
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

        if (session.GB == null || session.GB.Length == 0)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        if (!PhoneCallDhValidator.IsGaHashValid(obj.GA, session.GAHash))
        {
            RpcErrors.RpcErrors400.CallProtocolFlagsInvalid.ThrowRpcError();
            return null!;
        }

        if (!PhoneCallDhValidator.IsValidDhValue(obj.GA))
        {
            RpcErrors.RpcErrors400.CallProtocolFlagsInvalid.ThrowRpcError();
            return null!;
        }

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.GA, obj.GA)
            .Set(s => s.KeyFingerprint, obj.KeyFingerprint)
            .Set(s => s.State, "confirmed");

        await _callCollection.UpdateOneAsync(filter, update);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // R5.6: P2P (STUN) is only offered when BOTH peers' privacyKeyPhoneP2P settings
        // allow peer-to-peer connections. If either side disallows it, we fall back to
        // TURN reflectors only and leave p2p_allowed unset.
        var p2pAllowed = await IsP2pAllowedForBothPeersAsync(session.CallerId, session.CalleeId);

        var connections = new TVector<MyTelegram.Schema.IPhoneConnection>();
        var webRtcConnections = optionsAccessor.Value.WebRtcConnections;

        if (webRtcConnections != null && webRtcConnections.Count > 0)
        {
            long connectionId = 1;
            foreach (var webRtcConfig in webRtcConnections)
            {
                // R5.6: only include P2P STUN options when both peers allow P2P.
                if (webRtcConfig.Stun && p2pAllowed)
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

                // R5.5: always include TURN reflector connection options.
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

        if (!PhoneCallProtocolHelper.HasCommonLibraryVersion(session.CallerLibraryVersions, session.CalleeLibraryVersions))
        {
            RpcErrors.RpcErrors406.CallProtocolCompatLayerInvalid.ThrowRpcError();
            return null!;
        }

        var protocol = PhoneCallProtocolHelper.Negotiate(session.CallerLibraryVersions, session.CalleeLibraryVersions);
        var conferenceSupported = session.CallerConferenceSupported && session.CalleeConferenceSupported;
        var phoneCallForCaller = CreatePhoneCall(
            session,
            session.GetAccessHashForUser(session.CallerId),
            session.GB,
            obj.KeyFingerprint,
            protocol,
            currentDate,
            connections,
            conferenceSupported,
            p2pAllowed);
        var phoneCallForCallee = CreatePhoneCall(
            session,
            session.GetAccessHashForUser(session.CalleeId),
            obj.GA,
            obj.KeyFingerprint,
            protocol,
            currentDate,
            connections,
            conferenceSupported,
            p2pAllowed);

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallForCallee };

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
            PhoneCall = phoneCallForCaller,
            Users = usersVector
        };
    }

    private static Schema.TPhoneCall CreatePhoneCall(
        CallSessionDocument session,
        long accessHash,
        byte[] ga,
        long keyFingerprint,
        IPhoneCallProtocol protocol,
        int currentDate,
        TVector<MyTelegram.Schema.IPhoneConnection> connections,
        bool conferenceSupported,
        bool p2pAllowed)
    {
        return new Schema.TPhoneCall
        {
            Id = session.CallId,
            AccessHash = accessHash,
            AdminId = session.CallerId,
            ParticipantId = session.CalleeId,
            GAOrB = ga,
            KeyFingerprint = keyFingerprint,
            // R5.6: p2p_allowed reflects both peers' privacyKeyPhoneP2P privacy settings.
            P2pAllowed = p2pAllowed,
            ConferenceSupported = conferenceSupported,
            Protocol = protocol,
            Date = session.Date,
            StartDate = currentDate,
            Connections = connections,
            Video = session.Video
        };
    }

    // R5.6: peer-to-peer is only permitted when BOTH the caller and the callee allow
    // P2P via their privacyKeyPhoneP2P setting. ApplyPrivacyAsync invokes the callback
    // when the target user is not permitted by the self user's privacy rules; a fired
    // callback in either direction means P2P must be disabled.
    private async Task<bool> IsP2pAllowedForBothPeersAsync(long callerId, long calleeId)
    {
        var allowed = true;

        await privacyAppService.ApplyPrivacyAsync(callerId, calleeId, _ =>
        {
            allowed = false;
        }, PrivacyType.PhoneP2P);

        await privacyAppService.ApplyPrivacyAsync(calleeId, callerId, _ =>
        {
            allowed = false;
        }, PrivacyType.PhoneP2P);

        return allowed;
    }
}
