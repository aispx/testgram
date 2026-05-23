using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Invite a set of users to a group call.
/// Possible errors
/// Code Type Description
/// 403 CHAT_TYPE_INVALID The specified user type is invalid.
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 INVITE_FORBIDDEN_WITH_JOINAS If the user has anonymously joined a group call as a channel, they can't invite other users to the group call because that would cause deanonymization, because the invite would be sent using the original user ID, not the anonymized channel ID.
/// 400 USER_ALREADY_INVITED You have already invited this user.
/// 403 USER_NOT_PARTICIPANT You're not a member of this supergroup/channel.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.inviteToGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InviteToGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestInviteToGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestInviteToGroupCall obj)
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
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }

        foreach (var user in obj.Users)
        {
            var userId = peerHelper.GetPeer(user, input.UserId).PeerId;
            if (!groupCall.InvitedUserIds.Contains(userId))
            {
                groupCall.InvitedUserIds.Add(userId);
            }
        }

        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        var invitedUserIds = obj.Users.Select(user => peerHelper.GetPeer(user, input.UserId).PeerId).ToList();
        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId,
            invitedUserIds);
        return updates;
    }
}
