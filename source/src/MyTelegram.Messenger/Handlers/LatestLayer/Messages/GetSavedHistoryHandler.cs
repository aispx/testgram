namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetSavedHistoryHandler(
    IMessageAppService messageAppService,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedHistory, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedHistory obj)
    {

        if (obj.ParentPeer != null)
        {
            var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer);
            var topicPeer = peerHelper.GetPeer(obj.Peer);
            var monoforumReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
            if (monoforumReadModel == null || !monoforumReadModel.IsMonoforum || topicPeer == null)
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

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
                SavedPeerId = topicPeer
            });
            return getHistoryConverterService.ToMessages(input, r, input.Layer);
        }

        return new TMessages { Chats = new TVector<IChat>(), Messages = new TVector<IMessage>(), Users = new TVector<IUser>(), Topics = new TVector<IForumTopic>() };
    }
}
