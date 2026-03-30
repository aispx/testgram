using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class DiscardCallHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender)
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

        if (session.CallerId != input.UserId && session.CalleeId != input.UserId)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var reason = ConvertReason(obj.Reason);

        var update = Builders<CallSessionDocument>.Update
            .Set(s => s.State, "discarded")
            .Set(s => s.Duration, obj.Duration)
            .Set(s => s.DiscardReason, reason);

        await _callCollection.UpdateOneAsync(filter, update);

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var discardedCall = new Schema.TPhoneCallDiscarded
        {
            Id = session.CallId,
            Reason = obj.Reason,
            Duration = obj.Duration,
            NeedRating = true,
            NeedDebug = false,
            Video = session.Video
        };

        var users = await userConverterService.GetUserListAsync(input, new List<long> { session.CallerId, session.CalleeId }, false, false, input.Layer);
        var usersVector = new TVector<MyTelegram.Schema.IUser>(users);

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
}
