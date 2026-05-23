using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.QueryServer.EventHandlers;

public class MessengerEventHandler(
    IMessageQueueProcessor<MessengerQueryDataReceivedEvent> processor,
    IFileDownloadLaneRouter fileDownloadLaneRouter,
    IUserAccessHashKeyCache userAccessHashKeyCache)
    :
        IEventHandler<MessengerQueryDataReceivedEvent>,
        IEventHandler<StickerDataReceivedEvent>,
        IEventHandler<DownloadDataReceivedEvent>,
        IEventHandler<UploadDataReceivedEvent>,
        ITransientDependency
{
    public async Task HandleEventAsync(MessengerQueryDataReceivedEvent eventData)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);
        processor.Enqueue(eventData, eventData.PermAuthKeyId);
    }

    public async Task HandleEventAsync(StickerDataReceivedEvent eventData)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);
        processor.Enqueue(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId),
            eventData.AuthKeyId);
    }

    public async Task HandleEventAsync(UploadDataReceivedEvent eventData)
    {
        await TryRerouteGetCustomEmojiDocumentsAsync(eventData);
    }

    public async Task HandleEventAsync(DownloadDataReceivedEvent eventData)
    {
        if (await TryRerouteGetCustomEmojiDocumentsAsync(eventData))
        {
            return;
        }

        await fileDownloadLaneRouter.ForwardAsync(eventData);
    }

    private async Task<bool> TryRerouteGetCustomEmojiDocumentsAsync(DataReceivedEvent eventData)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);

        // messages.getCustomEmojiDocuments (0xd9ab0f54) is a messages.* RPC,
        // but older routing tables send it through file-server lanes.
        if (eventData.ObjectId != 0xd9ab0f54)
        {
            return false;
        }

        processor.Enqueue(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId),
            eventData.AuthKeyId);
        return true;
    }
}
