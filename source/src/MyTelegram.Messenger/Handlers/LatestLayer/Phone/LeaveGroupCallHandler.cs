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

        var participant = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId, obj.Source);
        if (participant != null)
        {
            groupCall.Participants.Remove(participant);
            groupCall.Version++;
            await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
            participant.Muted = true;
            participant.Left = true;
            var participantsUpdate = GroupCallStateHelper.Updates(
                GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]));
            await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
                objectMessageSender,
                groupCall,
                participantsUpdate,
                input.UserId,
                [input.UserId]);
            return GroupCallStateHelper.Updates(
                GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
        }

        return GroupCallStateHelper.Updates();
    }
}
