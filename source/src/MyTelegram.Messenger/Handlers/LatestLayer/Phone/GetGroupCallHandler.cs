using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCall, MyTelegram.Schema.Phone.IGroupCall>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.Phone.IGroupCall> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCall obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, inputGroupCall.Id),
            Builders<GroupCallDocument>.Filter.Eq(g => g.AccessHash, inputGroupCall.AccessHash)
        );

        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var call = new MyTelegram.Schema.TGroupCall
        {
            Id = groupCall.CallId,
            AccessHash = groupCall.AccessHash,
            ParticipantsCount = groupCall.Participants.Count,
            Version = groupCall.Version,
            JoinMuted = groupCall.JoinMuted,
            CanChangeJoinMuted = true,
            RtmpStream = groupCall.RtmpStream,
            Creator = false
        };

        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var participants = groupCall.Participants
            .Take(limit)
            .Select(p =>
            {
                var participant = new MyTelegram.Schema.TGroupCallParticipant
                {
                    Peer = new TPeerUser { UserId = p.PeerId },
                    Source = p.Source,
                    Date = p.Date,
                    Muted = p.Muted,
                    Left = false,
                    CanSelfUnmute = true,
                    Self = p.PeerId == input.UserId
                };
                if (p.VideoStopped)
                {
                    participant.VideoJoined = false;
                }
                return (MyTelegram.Schema.IGroupCallParticipant)participant;
            })
            .ToList();

        return new MyTelegram.Schema.Phone.TGroupCall
        {
            Call = call,
            Participants = new TVector<MyTelegram.Schema.IGroupCallParticipant>(participants),
            ParticipantsNextOffset = string.Empty,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}