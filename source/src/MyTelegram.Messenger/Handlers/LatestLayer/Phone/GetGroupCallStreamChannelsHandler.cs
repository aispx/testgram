using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get info about RTMP streams in a group call or livestream.<br/>
/// This method should be invoked to the same group/channel-related DC used for <a href="https://corefork.telegram.org/api/files#downloading-files">downloading livestream chunks</a>.<br/>
/// As usual, the media DC is preferred, if available.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_JOIN_MISSING You haven't joined this group call.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallStreamChannels"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallStreamChannelsHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallStreamChannels, MyTelegram.Schema.Phone.IGroupCallStreamChannels>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.Phone.IGroupCallStreamChannels> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallStreamChannels obj)
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
        if (!groupCall.Participants.Any(p => p.PeerId == input.UserId))
        {
            RpcErrors.RpcErrors400.GroupcallJoinMissing.ThrowRpcError();
            return null!;
        }

        var channels = new TVector<IGroupCallStreamChannel>
        {
            new TGroupCallStreamChannel
            {
                Channel = 0,
                Scale = 0,
                LastTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

        return new MyTelegram.Schema.Phone.TGroupCallStreamChannels { Channels = channels };
    }
}
