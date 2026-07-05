using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Upload;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.QueryServer.EventHandlers;

public class MessengerEventHandler(
    IMessageQueueProcessor<MessengerQueryDataReceivedEvent> processor,
    MessengerQueryDataProcessor queryDataProcessor,
    IFileDownloadLaneRouter fileDownloadLaneRouter,
    IUserAccessHashKeyCache userAccessHashKeyCache,
    IPushOnlineFilter pushOnlineFilter,
    ILogger<MessengerEventHandler> logger)
    :
        IEventHandler<MessengerQueryDataReceivedEvent>,
        IEventHandler<StickerDataReceivedEvent>,
        IEventHandler<DownloadDataReceivedEvent>,
        IEventHandler<UploadDataReceivedEvent>,
        ITransientDependency
{
    private const uint InputGroupCallStreamConstructorId = 0x0598a92a;

    public async Task HandleEventAsync(MessengerQueryDataReceivedEvent eventData)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);
        if (eventData.UserId != 0 && eventData.PermAuthKeyId != 0)
        {
            try { await pushOnlineFilter.MarkOnlineAsync(eventData.PermAuthKeyId); } catch { }
        }
        processor.Enqueue(eventData, eventData.PermAuthKeyId);
    }

    public async Task HandleEventAsync(StickerDataReceivedEvent eventData)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);
        if (await TryHandleStickerLaneGetFileAsync(eventData))
        {
            return;
        }

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

        if (await TryRerouteGroupCallStreamFileAsync(eventData, "upload lane"))
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

        if (await TryRerouteGroupCallStreamFileAsync(eventData, "download lane"))
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

    private async Task<bool> TryRerouteGroupCallStreamFileAsync(DataReceivedEvent eventData, string sourceLane)
    {
        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);

        if (!IsGetFileObjectId(eventData.ObjectId))
        {
            return false;
        }

        if (!IsGroupCallStreamGetFile(eventData))
        {
            return false;
        }

        logger.LogInformation(
            "Rerouting upload.getFile inputGroupCallStream from {SourceLane} to messenger query lane, reqMsgId: {ReqMsgId}",
            sourceLane,
            eventData.ReqMsgId);
        await ProcessGroupCallStreamGetFileAsync(eventData);
        return true;
    }

    private async Task<bool> TryHandleStickerLaneGetFileAsync(StickerDataReceivedEvent eventData)
    {
        if (!IsGetFileObjectId(eventData.ObjectId))
        {
            return false;
        }

        if (IsGroupCallStreamGetFile(eventData))
        {
            logger.LogInformation(
                "Rerouting upload.getFile inputGroupCallStream from sticker lane to messenger query lane, reqMsgId: {ReqMsgId}",
                eventData.ReqMsgId);
            await ProcessGroupCallStreamGetFileAsync(eventData);
            return true;
        }

        await fileDownloadLaneRouter.ForwardAsync(new UploadDataReceivedEvent(
            eventData.ConnectionId,
            eventData.ConnectionType,
            eventData.RequestId,
            eventData.ObjectId,
            eventData.UserId,
            eventData.ReqMsgId,
            eventData.SeqNumber,
            eventData.AuthKeyId,
            eventData.PermAuthKeyId,
            eventData.Data,
            eventData.Layer,
            eventData.Date,
            eventData.DeviceType,
            eventData.ClientIp,
            eventData.SessionId,
            eventData.AccessHashKeyId));
        return true;
    }

    private Task ProcessGroupCallStreamGetFileAsync(DataReceivedEvent eventData)
    {
        return queryDataProcessor.ProcessAsync(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId));
    }

    private bool IsGroupCallStreamGetFile(DataReceivedEvent eventData)
    {
        try
        {
            if (eventData.Data.ToTObject<IObject>() is RequestGetFile { Location: TInputGroupCallStream })
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to parse upload.getFile while checking group-call stream reroute, reqMsgId: {ReqMsgId}",
                eventData.ReqMsgId);
        }

        return ContainsConstructorId(eventData.Data.Span, InputGroupCallStreamConstructorId);
    }

    private static bool IsGetFileObjectId(uint objectId)
    {
        return objectId is ObjectIdConsts.GetFileObjectId or ObjectIdConsts.GetFileObjectIdLayer143;
    }

    private static bool ContainsConstructorId(ReadOnlySpan<byte> data, uint constructorId)
    {
        var bytes = BitConverter.GetBytes(constructorId);
        for (var i = 0; i <= data.Length - bytes.Length; i++)
        {
            if (data.Slice(i, bytes.Length).SequenceEqual(bytes))
            {
                return true;
            }
        }

        return false;
    }
}
