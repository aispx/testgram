using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Start or stop recording a group call: the recorded audio and video streams will be automatically sent to <code>Saved messages</code> (the chat with ourselves).
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_NOT_MODIFIED Group call settings weren't modified.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.toggleGroupCallRecord"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleGroupCallRecordHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestToggleGroupCallRecord, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestToggleGroupCallRecord obj)
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
        if (!groupCall.Active)
        {
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }
        if (groupCall.RecordVideoActive == obj.Start)
        {
            RpcErrors.RpcErrors400.GroupcallNotModified.ThrowRpcError();
            return null!;
        }

        groupCall.RecordVideoActive = obj.Start;
        groupCall.RecordStartDate = obj.Start ? GroupCallStateHelper.CurrentDate() : null;
        groupCall.RecordTitle = obj.Title;
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper));
    }
}
