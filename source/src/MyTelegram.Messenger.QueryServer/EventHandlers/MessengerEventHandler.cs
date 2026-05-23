using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Upload;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.QueryServer.EventHandlers;

public class MessengerEventHandler(
    IMessageQueueProcessor<MessengerQueryDataReceivedEvent> processor,
    IFileDownloadLaneRouter fileDownloadLaneRouter,
    IUserAccessHashKeyCache userAccessHashKeyCache,
    ILogger<MessengerEventHandler> logger)
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
        if (await TryRerouteGetCustomEmojiDocumentsAsync(eventData))
        {
            return;
        }

        if (await TryRerouteGroupCallStreamFileAsync(eventData))
        {
            return;
        }

        await fileDownloadLaneRouter.ForwardAsync(eventData);
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

    private async Task<bool> TryRerouteGroupCallStreamFileAsync(DataReceivedEvent eventData)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);

        if (eventData.ObjectId is not (ObjectIdConsts.GetFileObjectId or ObjectIdConsts.GetFileObjectIdLayer143))
        {
            return false;
        }

        if (eventData.Data.ToTObject<IObject>() is not RequestGetFile { Location: TInputGroupCallStream })
        {
            return false;
        }

        logger.LogInformation(
            "Rerouting upload.getFile inputGroupCallStream from file lane to messenger query lane, reqMsgId: {ReqMsgId}",
            eventData.ReqMsgId);
        processor.Enqueue(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId),
            eventData.AuthKeyId);
        return true;
    }
}
