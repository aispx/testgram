namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetSavedHistoryHandler(
    IMessageAppService messageAppService,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedHistory, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedHistory obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);

        if (obj.ParentPeer != null)
        {
            await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
            var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer);
            var topicPeer = peerHelper.GetPeer(obj.Peer);
            var r = await messageAppService.GetHistoryAsync(new GetHistoryInput
            {
                OwnerPeerId = monoforumPeer.PeerId,
                SelfUserId = input.UserId,
                AddOffset = obj.AddOffset,
                Limit = obj.Limit,
                MaxId = obj.MaxId,
                MinId = obj.MinId,
                OffsetId = obj.OffsetId,
                Peer = monoforumPeer,
                FilterSenderUserId = topicPeer?.PeerId ?? 0
            });
            return getHistoryConverterService.ToMessages(input, r, input.Layer);
        }

        return new TMessages { Chats = [], Messages = [], Users = [], Topics = [] };
    }
}
