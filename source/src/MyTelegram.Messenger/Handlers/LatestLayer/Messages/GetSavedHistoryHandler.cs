namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetSavedHistoryHandler(
    IMessageAppService messageAppService,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedHistory, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedHistory obj)
    {

        if (obj.ParentPeer != null)
        {
            var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
            var topicPeer = peerHelper.GetPeer(obj.Peer, input.UserId);
            var monoforumReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
            if (monoforumReadModel == null || !monoforumReadModel.IsMonoforum || topicPeer == null)
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

            // The topic peer is client-supplied: without this check any user could name a third party
            // and read that person's private conversation with the channel.
            await MonoforumAccessHelper.EnsureCanReadTopicAsync(
                monoforumReadModel!, topicPeer, input.UserId, channelAppService);

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
