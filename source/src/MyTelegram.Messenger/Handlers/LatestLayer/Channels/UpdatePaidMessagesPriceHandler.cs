namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
internal sealed class UpdatePaidMessagesPriceHandler(
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IRandomHelper randomHelper,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdatePaidMessagesPrice, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestUpdatePaidMessagesPrice obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Channel);
        var channelPeer = peerHelper.GetChannel(obj.Channel);
        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channelPeer.PeerId));
        if (channelReadModel == null || !channelReadModel.Broadcast)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (channelReadModel!.IsMonoforum)
            RpcErrors.RpcErrors400.ChannelMonoforumUnsupported.ThrowRpcError();

        try
        {
            if (obj.BroadcastMessagesAllowed && !channelReadModel.BroadcastMessagesAllowed)
            {
                var monoforumId = await idGenerator.NextLongIdAsync(IdType.ChannelId);
                var accessHash = randomHelper.NextInt64();
                var date = DateTime.UtcNow.ToTimestamp();
                var title = channelReadModel.Title;
                var req = input.ToRequestInfo();

                await commandBus.PublishAsync(new CreateChannelCommand(
                    ChannelId.Create(monoforumId),
                    req,
                    monoforumId, input.UserId, false, true, title, string.Empty,
                    null, null, accessHash, date, randomHelper.NextInt64(),
                    new TMessageActionChannelCreate { Title = title },
                    null, false, null, null, null));

                await commandBus.PublishAsync(new EnableMonoforumCommand(
                    ChannelId.Create(monoforumId),
                    req with { ReqMsgId = req.ReqMsgId + 1, RequestId = Guid.NewGuid() },
                    monoforumId, isMonoforum: true, broadcastMessagesAllowed: false,
                    linkedMonoforumId: channelPeer.PeerId));

                await commandBus.PublishAsync(new EnableMonoforumCommand(
                    ChannelId.Create(channelPeer.PeerId),
                    req with { ReqMsgId = req.ReqMsgId + 2, RequestId = Guid.NewGuid() },
                    channelPeer.PeerId, isMonoforum: false, broadcastMessagesAllowed: true,
                    linkedMonoforumId: monoforumId));

                // Send service message to channel
                await SendPaidMessagesPriceServiceMessageAsync(input, channelPeer.PeerId, broadcastMessagesAllowed: true, stars: obj.SendPaidMessagesStars);
            }
            else if (!obj.BroadcastMessagesAllowed && channelReadModel.BroadcastMessagesAllowed)
            {
                await commandBus.PublishAsync(new EnableMonoforumCommand(
                    ChannelId.Create(channelPeer.PeerId),
                    input.ToRequestInfo(),
                    channelPeer.PeerId, isMonoforum: false, broadcastMessagesAllowed: false,
                    linkedMonoforumId: null));

                // Send service message to channel
                await SendPaidMessagesPriceServiceMessageAsync(input, channelPeer.PeerId, broadcastMessagesAllowed: false, stars: 0);
            }
            else if (obj.BroadcastMessagesAllowed && channelReadModel.BroadcastMessagesAllowed)
            {
                // Price changed
                await SendPaidMessagesPriceServiceMessageAsync(input, channelPeer.PeerId, broadcastMessagesAllowed: true, stars: obj.SendPaidMessagesStars);
            }
        }
        catch (Exception ex) when (ex.GetType().Name == "DuplicateOperationException")
        {
            // Idempotent retry — already processed
        }

        return new TUpdates { Chats = [], Updates = [], Users = [], Date = DateTime.UtcNow.ToTimestamp() };
    }

    private async Task SendPaidMessagesPriceServiceMessageAsync(IRequestInput input, long channelId, bool broadcastMessagesAllowed, long stars)
    {
        var sendInput = new SendMessageInput(
            input.ToRequestInfo(),
            senderUserId: input.UserId,
            toPeer: new Peer(PeerType.Channel, channelId),
            message: string.Empty,
            randomId: new Random().NextInt64(),
            messageAction: new TMessageActionPaidMessagesPrice
            {
                BroadcastMessagesAllowed = broadcastMessagesAllowed,
                Stars = stars
            },
            sendMessageType: SendMessageType.MessageService
        );
        await messageAppService.SendMessageAsync([sendInput]);
    }
}
