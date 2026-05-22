using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Broadcast a blockchain block to all members of a conference call, see <a href="https://corefork.telegram.org/api/end-to-end/group-calls">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.sendConferenceCallBroadcast"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendConferenceCallBroadcastHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestSendConferenceCallBroadcast, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestSendConferenceCallBroadcast obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(inputGroupCall);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        groupCall.ChainBlocks.Add(new GroupCallChainBlockDoc { Block = obj.Block.ToArray() });
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        return GroupCallStateHelper.Updates(new TUpdateGroupCallChainBlocks
        {
            Call = GroupCallStateHelper.ToInputGroupCall(groupCall),
            Blocks = new TVector<ReadOnlyMemory<byte>>([obj.Block]),
            NextOffset = groupCall.ChainBlocks.Count,
            SubChainId = 0
        });
    }
}
