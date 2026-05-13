using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get dialogs manually marked as unread
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDialogUnreadMarks"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDialogUnreadMarksHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetDialogUnreadMarks, TVector<MyTelegram.Schema.IDialogPeer>>
{
    protected override async Task<TVector<MyTelegram.Schema.IDialogPeer>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetDialogUnreadMarks obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
        if (obj.ParentPeer == null)
        {
            return [];
        }

        var parentPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
        if (parentPeer.PeerType != PeerType.Channel)
        {
            return [];
        }

        var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(parentPeer.PeerId));
        if (monoforum == null || !monoforum.IsMonoforum)
        {
            return [];
        }

        var peers = await MonoForumTopicStateHelper.GetUnreadMarkedPeersAsync(mongoDatabase, parentPeer.PeerId, input.UserId);
        return new TVector<IDialogPeer>(peers.Select(p => (IDialogPeer)new TDialogPeer { Peer = GetSavedDialogsHandler.ToSchemaPeer(p) }));
    }
}
