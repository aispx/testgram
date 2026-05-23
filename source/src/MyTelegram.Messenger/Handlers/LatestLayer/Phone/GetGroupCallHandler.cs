using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IAccessHashHelper2 accessHashHelper2)
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

        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(inputGroupCall)).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        if (!groupCall.Active)
        {
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }

        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var participants = groupCall.Participants
            .Take(limit)
            .Select(p => (MyTelegram.Schema.IGroupCallParticipant)GroupCallStateHelper.ToParticipant(p, input.UserId, peerHelper))
            .ToList();

        return new MyTelegram.Schema.Phone.TGroupCall
        {
            Call = GroupCallStateHelper.ToGroupCall(
                groupCall,
                input.UserId,
                accessHashHelper2.GenerateAccessHash(input.UserId, input.AccessHashKeyId, groupCall.CallId, AccessHashType.GroupCall)),
            Participants = new TVector<MyTelegram.Schema.IGroupCallParticipant>(participants),
            ParticipantsNextOffset = groupCall.Participants.Count > limit ? limit.ToString() : string.Empty,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}
