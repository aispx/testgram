using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class LeaveGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<RequestLeaveGroupCall, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestLeaveGroupCall obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input.UserId);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var participant = groupCall.Participants.FirstOrDefault(p => p.PeerId == input.UserId && p.Source == obj.Source);
        if (participant != null)
        {
            groupCall.Participants.Remove(participant);
            groupCall.Version++;
            await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
            participant.Muted = true;
            participant.Left = true;
            var updates = GroupCallStateHelper.Updates(
                GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]));
            await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
                objectMessageSender,
                groupCall,
                updates,
                input.UserId,
                [input.UserId]);
            return updates;
        }

        return GroupCallStateHelper.Updates();
    }
}
