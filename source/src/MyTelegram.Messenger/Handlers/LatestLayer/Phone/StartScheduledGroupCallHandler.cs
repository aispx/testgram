using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Start a scheduled group call.
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_ALREADY_STARTED The groupcall has already started, you can join directly using <a href="https://corefork.telegram.org/method/phone.joinGroupCall">phone.joinGroupCall</a>.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.startScheduledGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class StartScheduledGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestStartScheduledGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestStartScheduledGroupCall obj)
    {
        if (obj.Call is not MyTelegram.Schema.TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(inputGroupCall);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!groupCall.ScheduleDate.HasValue)
        {
            RpcErrors.RpcErrors403.GroupcallAlreadyStarted.ThrowRpcError();
            return null!;
        }

        groupCall.ScheduleDate = null;
        groupCall.ScheduleStartSubscriberIds.Clear();
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
    }
}
