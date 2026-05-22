namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using System.Globalization;
using MyTelegram.Domain.Aggregates.UserConfig;
/// <summary>
/// Mark <a href="https://corefork.telegram.org/api/reactions">message reactions »</a> as read
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readReactions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadReactionsHandler(
    IPtsHelper ptsHelper,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadReactions, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        await accessHashHelper.CheckAccessHashAsync(input, obj.SavedPeerId);
        var savedPeer = obj.SavedPeerId == null ? null : peerHelper.GetPeer(obj.SavedPeerId, input.UserId);
        var key = ReactionReadState.GetKey(peer, obj.TopMsgId, savedPeer);
        var command = new UpdateUserConfigCommand(
            UserConfigId.Create(input.UserId, key),
            input.ToRequestInfo(),
            input.UserId,
            key,
            CurrentDate.ToString(CultureInfo.InvariantCulture));
        await commandBus.PublishAsync(command);

        return new TAffectedHistory
        {
            Pts = ptsHelper.GetCachedPts(peer.PeerId),
            PtsCount = 0
        };
    }
}
