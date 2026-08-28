using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class DiscardCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    ITopPeerUsageRecorder topPeerUsageRecorder,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDiscardCall, IUpdates>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDiscardCall obj)
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

        if (session.CallerId != input.UserId && session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

        // Requirement 7.9: idempotent re-discard - return the existing phoneCallDiscarded
        // without mutating recorded state and without re-pushing updates / service messages.
        if (session.State == CallSessionStates.Discarded)
        {
            var existingDiscardedCall = new Schema.TPhoneCallDiscarded
            {
                Id = session.CallId,
                Reason = BuildStoredReason(session.DiscardReason, session.DiscardReasonSlug),
                Duration = session.Duration,
                NeedRating = session.NeedRating,
                NeedDebug = session.NeedDebug,
                Video = session.Video
            };

            return new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = existingDiscardedCall } },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            };
        }

        var reason = ConvertReason(obj.Reason);
        var reasonSlug = (obj.Reason as TPhoneCallDiscardReasonMigrateConferenceCall)?.Slug;

        // Requirement 7.4/7.5: rating/debug policy - a call is rateable/debuggable only if it
        // reached the connected (confirmed) state; rating additionally requires a non-zero duration.
        var needRating = session.State == CallSessionStates.Confirmed && obj.Duration > 0;
        var needDebug = session.State == CallSessionStates.Confirmed;

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.State, CallSessionStates.Discarded)
            .Set(s => s.StateChangedDate, currentDate)
            .Set(s => s.Duration, obj.Duration)
            .Set(s => s.DiscardReason, reason)
            .Set(s => s.DiscardReasonSlug, reasonSlug)
            .Set(s => s.NeedRating, needRating)
            .Set(s => s.NeedDebug, needDebug)
            .Set(s => s.Video, session.Video || obj.Video);

        await _callCollection.UpdateOneAsync(filter, update);

        var discardedCall = new Schema.TPhoneCallDiscarded
        {
            Id = session.CallId,
            Reason = obj.Reason,
            Duration = obj.Duration,
            NeedRating = needRating,
            NeedDebug = needDebug,
            Video = session.Video || obj.Video
        };

        var updatePhoneCall = new TUpdatePhoneCall { PhoneCall = discardedCall };

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

        // The discarding user's OTHER devices must stop ringing / tear down their controller too, so they
        // get the same phoneCallDiscarded. The device that issued phone.discardCall already has the result
        // of this very request and is excluded via excludeAuthKeyId (same pattern as AcceptCallHandler).
        var selfPeer = new Peer(PeerType.User, input.UserId);
        await objectMessageSender.PushMessageToPeerAsync(selfPeer,
            new TUpdates
            {
                Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = discardedCall } },
                Users = usersVector,
                Chats = new TVector<IChat>(),
                Date = currentDate
            },
            excludeAuthKeyId: input.AuthKeyId);

        await SendCallDiscardedServiceMessageAsync(
            input,
            session.CallId,
            session.CallerId,
            session.CalleeId,
            obj.Duration,
            obj.Reason,
            session.Video || obj.Video);

        // A finished call is what topPeerCategoryPhoneCalls ranks, and it counts for both parties: the
        // other side becomes a likely call destination for each of them. Recorded here rather than at
        // phone.requestCall so a call is counted once — the idempotent re-discard above returns early.
        // See https://corefork.telegram.org/api/top-rating
        await topPeerUsageRecorder.RecordAsync(session.CallerId, TopPeerCategory.PhoneCalls, PeerType.User,
            session.CalleeId);
        await topPeerUsageRecorder.RecordAsync(session.CalleeId, TopPeerCategory.PhoneCalls, PeerType.User,
            session.CallerId);

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updatePhoneCall },
            Users = usersVector,
            Chats = new TVector<IChat>(),
            Date = currentDate
        };
    }

    private static string? ConvertReason(IPhoneCallDiscardReason? reason)
    {
        return reason switch
        {
            TPhoneCallDiscardReasonMissed => "missed",
            TPhoneCallDiscardReasonDisconnect => "disconnect",
            TPhoneCallDiscardReasonHangup => "hangup",
            TPhoneCallDiscardReasonBusy => "busy",
            TPhoneCallDiscardReasonMigrateConferenceCall => "migrate",
            _ => null
        };
    }

    private static IPhoneCallDiscardReason? BuildStoredReason(string? reason, string? slug)
    {
        return reason switch
        {
            "missed" => new TPhoneCallDiscardReasonMissed(),
            "disconnect" => new TPhoneCallDiscardReasonDisconnect(),
            "hangup" => new TPhoneCallDiscardReasonHangup(),
            "busy" => new TPhoneCallDiscardReasonBusy(),
            "migrate" => new TPhoneCallDiscardReasonMigrateConferenceCall { Slug = slug ?? string.Empty },
            _ => null
        };
    }

    private async Task SendCallDiscardedServiceMessageAsync(
        IRequestInput input,
        long callId,
        long callerId,
        long calleeId,
        int? duration,
        IPhoneCallDiscardReason? reason,
        bool video)
    {
        var isCaller = input.UserId == callerId;
        var targetUserId = isCaller ? calleeId : callerId;

        var action = new TMessageActionPhoneCall
        {
            CallId = callId,
            Reason = reason,
            Duration = duration,
            Video = video
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.User, targetUserId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.PhoneCall,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);
    }
}
