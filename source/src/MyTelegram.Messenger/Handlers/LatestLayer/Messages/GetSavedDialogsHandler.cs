namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetSavedDialogsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IUserConverterService userConverterService,
    IMessageAppService messageAppService,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedDialogs, MyTelegram.Schema.Messages.ISavedDialogs>
{
    protected override async Task<MyTelegram.Schema.Messages.ISavedDialogs> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedDialogs obj)
    {
        if (obj.ParentPeer != null)
        {
            await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
            var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer);
            var monoforumReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
            if (monoforumReadModel == null || !monoforumReadModel.IsMonoforum)
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

            var limit = obj.Limit > 0 ? obj.Limit : 20;
            var dialogs = await queryProcessor.ProcessAsync(new GetMonoforumDialogsQuery(monoforumPeer.PeerId, limit, obj.OffsetId));

            var userIds = dialogs.Select(d => d.OwnerId).Distinct().ToList();
            var users = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(userIds));
            var contacts = await queryProcessor.ProcessAsync(new GetContactListQuery(input.UserId, userIds));
            var photoIds = users.Where(u => u.ProfilePhotoId.HasValue).Select(u => u.ProfilePhotoId!.Value).ToList();
            var photos = photoIds.Count > 0
                ? await queryProcessor.ProcessAsync(new GetPhotoListQuery(input.UserId, photoIds))
                : Array.Empty<IPhotoReadModel>();
            var userList = userConverterService.ToUserList(input, users, photos, contacts, [], input.Layer);

            // Load top messages
            var topMessageIds = dialogs.Where(d => d.TopMessage > 0).Select(d => d.TopMessage).Distinct().ToList();
            List<IMessage> messageList = [];
            if (topMessageIds.Count > 0)
            {
                var msgOutput = await messageAppService.GetChannelDifferenceAsync(new GetDifferenceInput(
                    input.UserId,
                    monoforumPeer.PeerId,
                    0,
                    topMessageIds.Count,
                    topMessageIds));
                var converted = getHistoryConverterService.ToMessages(input, msgOutput, input.Layer);
                if (converted is TMessages tm) messageList = tm.Messages.ToList();
                else if (converted is TChannelMessages tcm) messageList = tcm.Messages.ToList();
            }

            var monoDialogs = dialogs.Select(d => new TMonoForumDialog
            {
                Peer = new TPeerUser { UserId = d.OwnerId },
                TopMessage = d.TopMessage,
                ReadInboxMaxId = d.ReadInboxMaxId,
                ReadOutboxMaxId = d.ReadOutboxMaxId,
                UnreadCount = d.UnreadCount,
                UnreadReactionsCount = d.UnreadReactionsCount
            }).ToList<ISavedDialog>();

            return new TSavedDialogs
            {
                Dialogs = [.. monoDialogs],
                Messages = [.. messageList],
                Chats = [],
                Users = [.. userList]
            };
        }

        return new TSavedDialogs { Chats = [], Dialogs = [], Messages = [], Users = [] };
    }
}
