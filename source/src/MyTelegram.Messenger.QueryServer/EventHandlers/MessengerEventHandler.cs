namespace MyTelegram.Messenger.QueryServer.EventHandlers;

public class MessengerEventHandler(
    IMessageQueueProcessor<MessengerQueryDataReceivedEvent> processor,
    IFileDownloadLaneRouter fileDownloadLaneRouter,
    ILogger<MessengerEventHandler> logger)
    :
        IEventHandler<MessengerQueryDataReceivedEvent>,
        IEventHandler<StickerDataReceivedEvent>,
        IEventHandler<DownloadDataReceivedEvent>,
        IEventHandler<UploadDataReceivedEvent>,
        ITransientDependency
{
    public Task HandleEventAsync(MessengerQueryDataReceivedEvent eventData)
    {
        processor.Enqueue(eventData, eventData.PermAuthKeyId);
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(StickerDataReceivedEvent eventData)
    {
        processor.Enqueue(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId),
            eventData.AuthKeyId);
        return Task.CompletedTask;
    }

    public async Task HandleEventAsync(UploadDataReceivedEvent eventData)
    {
        await TryRerouteGetCustomEmojiDocumentsAsync(eventData, "upload");
    }

    public async Task HandleEventAsync(DownloadDataReceivedEvent eventData)
    {
        if (await TryRerouteGetCustomEmojiDocumentsAsync(eventData, "download"))
        {
            return;
        }

        await fileDownloadLaneRouter.ForwardAsync(eventData);
    }

    private Task<bool> TryRerouteGetCustomEmojiDocumentsAsync(DataReceivedEvent eventData, string sourceLane)
    {
        // messages.getCustomEmojiDocuments (0xd9ab0f54) is a messages.* RPC,
        // but older routing tables send it through file-server lanes.
        if (eventData.ObjectId != 0xd9ab0f54)
        {
            return Task.FromResult(false);
        }

        logger.LogInformation(
            "Rerouting getCustomEmojiDocuments from {SourceLane} lane to messenger query lane, reqMsgId: {ReqMsgId}",
            sourceLane,
            eventData.ReqMsgId);
        processor.Enqueue(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId),
            eventData.AuthKeyId);
        return Task.FromResult(true);
    }
}
