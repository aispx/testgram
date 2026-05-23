using MongoDB.Driver;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class RequestCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    IUserAccessHashKeyCache userAccessHashKeyCache,
    IAccessHashHelper2 accessHashHelper2)
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

        ValidateHandshakeProtocol(obj.Protocol);

        var duplicateFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, input.UserId),
            Builders<CallSessionDocument>.Filter.Eq(s => s.RandomId, obj.RandomId));
        if (await _callCollection.Find(duplicateFilter).AnyAsync())
        {
            RpcErrors.RpcErrors500.RandomIdDuplicate.ThrowRpcError();
            return null!;
        }

        var busyFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CalleeId, calleeId),
            Builders<CallSessionDocument>.Filter.In(s => s.State, ["received", "accepted", "confirmed"]));
        if (await _callCollection.Find(busyFilter).AnyAsync())
        {
            RpcErrors.RpcErrors400.CallOccupyFailed.ThrowRpcError();
            return null!;
        }

        var callId = Random.Shared.NextInt64();
        callId = Math.Abs(callId);
        if (callId == 0) callId = 1;

        await userAccessHashKeyCache.RememberAsync(input.UserId, input.AccessHashKeyId);

        var callerAccessHash = accessHashHelper2.GenerateAccessHash(input.UserId, input.AccessHashKeyId, callId, AccessHashType.Call);
        var calleeAccessHash = await CreateCallAccessHashForUserAsync(calleeId, callId);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var session = new CallSessionDocument
        {
            Id = callId,
            CallId = callId,
            AccessHash = callerAccessHash,
            CallerAccessHash = callerAccessHash,
            CalleeAccessHash = calleeAccessHash,
            CallerId = input.UserId,
            CalleeId = calleeId,
            RandomId = obj.RandomId,
            GAHash = obj.GAHash,
            Video = obj.Video,
            State = "requested",
            Date = currentDate,
            CallerLibraryVersions = [.. PhoneCallProtocolHelper.GetLibraryVersions(obj.Protocol)]
        };
        await _callCollection.InsertOneAsync(session);

        var phoneCallWaiting = new MyTelegram.Schema.TPhoneCallWaiting
        {
            Id = callId,
            AccessHash = callerAccessHash,
            AdminId = input.UserId,
            ParticipantId = calleeId,
            Date = currentDate,
            Protocol = PhoneCallProtocolHelper.Normalize(obj.Protocol),
            Video = obj.Video
        };

        var phoneCallRequested = new MyTelegram.Schema.TPhoneCallRequested
        {
            Id = callId,
            AccessHash = calleeAccessHash,
            AdminId = input.UserId,
            ParticipantId = calleeId,
            GAHash = obj.GAHash,
            Date = currentDate,
            Protocol = PhoneCallProtocolHelper.Normalize(obj.Protocol),
            Video = obj.Video
        };

        var users = await userConverterService.GetUserListAsync(input, new List<long> { input.UserId, calleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        var updatePhoneCall = new MyTelegram.Schema.TUpdatePhoneCall { PhoneCall = phoneCallRequested };
        var incomingCallUpdates = new TUpdates
        {
            Updates = new TVector<IUpdate> { updatePhoneCall },
            Users = usersVector,
            Chats = new TVector<IChat>(),
            Date = currentDate
        };

        var calleePeer = new Peer(PeerType.User, calleeId);
        await objectMessageSender.PushMessageToPeerAsync(
            calleePeer,
            incomingCallUpdates,
            pushData: CreateIncomingCallPushData(input.UserId, calleeId, callId, calleeAccessHash, incomingCallUpdates, users));

        await SendIncomingCallServiceMessageAsync(input, callId, calleeId, obj.Video);

        return new MyTelegram.Schema.Phone.TPhoneCall
        {
            PhoneCall = phoneCallWaiting,
            Users = usersVector
        };
    }

    private async Task<long> CreateCallAccessHashForUserAsync(long userId, long callId)
    {
        var accessHashKeyId = await userAccessHashKeyCache.GetAsync(userId);
        if (accessHashKeyId.HasValue)
        {
            return accessHashHelper2.GenerateAccessHash(userId, accessHashKeyId.Value, callId, AccessHashType.Call);
        }

        var fallbackAccessHash = Random.Shared.NextInt64();
        fallbackAccessHash = Math.Abs(fallbackAccessHash);
        return fallbackAccessHash == 0 ? 1 : fallbackAccessHash;
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

    private async Task SendIncomingCallServiceMessageAsync(IRequestInput input, long callId, long calleeId, bool video)
    {
        var action = new TMessageActionPhoneCall
        {
            CallId = callId,
            Video = video
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.User, calleeId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);
    }

    private static PushData CreateIncomingCallPushData(
        long callerId,
        long calleeId,
        long callId,
        long accessHash,
        TUpdates updates,
        IReadOnlyCollection<IUser> users)
    {
        var callerName = callerId.ToString();
        var caller = users.OfType<TUser>().FirstOrDefault(user => user.Id == callerId);
        if (caller != null)
        {
            callerName = string.IsNullOrWhiteSpace(caller.LastName)
                ? caller.FirstName ?? callerName
                : $"{caller.FirstName} {caller.LastName}".Trim();
        }

        return new PushData(
            PushNotificationTypes.PhoneCallRequest,
            [callerName],
            calleeId,
            new PushNotificationCustomData
            {
                Updates = updates.ToBytes().ToBase64Url(),
                CallId = callId,
                CallAh = accessHash
            },
            null);
    }
}
