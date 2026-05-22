using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Fetch the blocks of a <a href="https://corefork.telegram.org/api/end-to-end/group-calls">conference blockchain »</a>.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallChainBlocks"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallChainBlocksHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallChainBlocks, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallChainBlocks obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(inputGroupCall)).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var offset = Math.Max(0, obj.Offset);
        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var blocks = groupCall.ChainBlocks
            .Where(block => block.SubChainId == obj.SubChainId)
            .Skip(offset)
            .Take(limit)
            .Select(block => (ReadOnlyMemory<byte>)block.Block)
            .ToList();

        return GroupCallStateHelper.Updates(new TUpdateGroupCallChainBlocks
        {
            Call = GroupCallStateHelper.ToInputGroupCall(groupCall),
            Blocks = new TVector<ReadOnlyMemory<byte>>(blocks),
            NextOffset = offset + blocks.Count,
            SubChainId = obj.SubChainId
        });
    }
}
