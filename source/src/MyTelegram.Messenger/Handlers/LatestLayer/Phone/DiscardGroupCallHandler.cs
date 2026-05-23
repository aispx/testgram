using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Terminate a group call
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_ALREADY_DISCARDED The group call was already discarded.
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.discardGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DiscardGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDiscardGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDiscardGroupCall obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input.UserId);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallAlreadyDiscarded.ThrowRpcError();
            return null!;
        }

        groupCall.Active = false;
        groupCall.Version++;
        var date = GroupCallStateHelper.CurrentDate();
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        var updates = GroupCallStateHelper.Updates(new TUpdateGroupCall
        {
            Peer = peerHelper.ToPeer((PeerType)groupCall.PeerType, groupCall.PeerId),
            Call = GroupCallStateHelper.ToDiscardedGroupCall(groupCall, date)
        });
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId,
            groupCall.InvitedUserIds);
        return updates;
    }
}
