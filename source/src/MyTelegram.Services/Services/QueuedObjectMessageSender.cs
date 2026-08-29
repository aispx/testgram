using System.Collections;

namespace MyTelegram.Services.Services;

public class QueuedObjectMessageSender(
    IMessageQueueProcessor<ISessionMessage> sessionMessageQueueProcessor,
    IAccessHashHelper2 accessHashHelper2,
    IFileReferenceStamper fileReferenceStamper)
    : IObjectMessageSender, ITransientDependency
{
    public Task PushSessionMessageToAuthKeyIdAsync<TData>(long authKeyId,
        TData data,
        int pts = 0,
        int? qts = null,
        long globalSeqNo = 0) where TData : IObject
    {
        // Push updates are serialized here and never pass through RpcResultObjectHandler,
        // so normalize any invalid media DcId (<=0) before it reaches the client. Otherwise
        // the client hot-loops help.getConfig and floods the log with "skip queue: unknown dc".
        DcIdNormalizer.Normalize(data, nameof(PushSessionMessageToAuthKeyIdAsync));
        fileReferenceStamper.Stamp(data);

        sessionMessageQueueProcessor.Enqueue(new LayeredAuthKeyIdMessageCreatedIntegrationEvent(
            authKeyId,
                data.ToBytes(),
                pts,
                qts,
                globalSeqNo),
            authKeyId);

        return Task.CompletedTask;
    }

    public Task PushMessageToPeerAsync<TData>(Peer peer,
        TData data,
        long? excludeAuthKeyId = null,
        long? excludeUserId = null,
        long? onlySendToUserId = null,
        long? onlySendToThisAuthKeyId = null,
        int pts = 0,
        int? qts = null,
        long globalSeqNo = 0,
        PushData? pushData = null,
        List<long>? excludeUserIds = null
        ) where TData : IObject
    {
        // See PushSessionMessageToAuthKeyIdAsync: updates pushed to peers bypass the
        // RpcResultObjectHandler safety net, so normalize invalid media DcId here too.
        DcIdNormalizer.Normalize(data, nameof(PushMessageToPeerAsync));
        fileReferenceStamper.Stamp(data);

        sessionMessageQueueProcessor.Enqueue(new LayeredPushMessageCreatedIntegrationEvent(peer.PeerType,
                peer.PeerId,
                data.ToBytes(),
                excludeAuthKeyId,
                excludeUserId,
                onlySendToUserId,
                onlySendToThisAuthKeyId,
                pts,
                qts,
                globalSeqNo,
                PushData: pushData,
                excludeUserIds
            ),
            peer.PeerId);

        return Task.CompletedTask;
    }

    public Task SendMessageToPeerAsync<TData>(RequestInfo requestInfo,
        TData data) where TData : IObject
    {
        UpdateAccessHashIfNeeded(requestInfo, data);
        fileReferenceStamper.Stamp(data);

        sessionMessageQueueProcessor.Enqueue(new DataResultResponseReceivedEvent(requestInfo.ConnectionId, requestInfo.AuthKeyId, requestInfo.SessionId, requestInfo.ReqMsgId, Array.Empty<byte>())
        {
            DataObject = data
        },
            requestInfo.PermAuthKeyId);

        return Task.CompletedTask;
    }

    public Task SendFileDataToPeerAsync<TData>(RequestInfo requestInfo,
        TData data) where TData : IObject
    {
        UpdateAccessHashIfNeeded(requestInfo, data);
        fileReferenceStamper.Stamp(data);

        sessionMessageQueueProcessor.Enqueue(new FileDataResultResponseReceivedEvent(requestInfo.ConnectionId, requestInfo.AuthKeyId, requestInfo.SessionId, requestInfo.ReqMsgId, data.ToBytes()),
            requestInfo.PermAuthKeyId);

        return Task.CompletedTask;
    }

    public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo,
        TData data,
        int pts = 0) where TData : IObject
    {
        UpdateAccessHashIfNeeded(requestInfo, data);

        return SendRpcMessageToClientAsync(requestInfo.ConnectionId, requestInfo.AuthKeyId, requestInfo.SessionId, requestInfo.ReqMsgId, data, pts, requestInfo.PermAuthKeyId);
    }

    public Task SendRpcMessageToClientAsync<TData>(
        string connectionId,
        long tempAuthKeyId,
        long sessionId,
        long reqMsgId, TData data, int pts = 0, long permAuthKeyId = 0) where TData : IObject
    {
        // Updates built by domain event handlers reach the client through this path without
        // passing through RpcResultObjectHandler; normalize invalid media DcId before sending.
        DcIdNormalizer.Normalize(data, nameof(SendRpcMessageToClientAsync));
        fileReferenceStamper.Stamp(data);

        var rpcResult = CreateRpcResult(reqMsgId, data);

        sessionMessageQueueProcessor.Enqueue(new DataResultResponseReceivedEvent(connectionId, tempAuthKeyId, sessionId, reqMsgId, Array.Empty<byte>())
        {
            DataObject = rpcResult
        },
            permAuthKeyId);

        return Task.CompletedTask;
    }

    public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data,
        long authKeyId, long permAuthKeyId, long userId,
        int pts = 0) where TData : IObject
    {
        UpdateAccessHashIfNeeded(requestInfo, data);
        DcIdNormalizer.Normalize(data, nameof(SendRpcMessageToClientAsync));
        fileReferenceStamper.Stamp(data);

        var rpcResult = CreateRpcResult(requestInfo.ReqMsgId, data);
        sessionMessageQueueProcessor.Enqueue(
            new DataResultResponseWithUserIdReceivedEvent(requestInfo.ConnectionId, requestInfo.AuthKeyId, requestInfo.SessionId, requestInfo.ReqMsgId, rpcResult.ToBytes(), userId, authKeyId,
                permAuthKeyId),
            requestInfo.PermAuthKeyId);

        return Task.CompletedTask;
    }

    private TRpcResult CreateRpcResult<TData>(long reqMsgId, TData data) where TData : IObject
    {
        //var newData = data;
        var rpcResult = new TRpcResult { ReqMsgId = reqMsgId, Result = data };

        //var length = data.GetLength();
        //if (length > 500)
        //{
        //    var gzipPacked = new TGzipPacked
        //    {
        //        PackedData = gzipHelper.Compress(newData.ToBytes())
        //    };
        //    rpcResult.Result = gzipPacked;
        //}

        return rpcResult;
    }

    private void UpdateAccessHashIfNeeded(RequestInfo requestInfo, IObject data)
    {
        UpdateAccessHashIfNeeded(requestInfo, (object?)data);
    }

    private void UpdateAccessHashIfNeeded(RequestInfo requestInfo, object? data)
    {
        if (data == null || data is string || data is byte[])
        {
            return;
        }

        if (data is IHasAccessHash hasAccessHash)
        {
            UpdateAccessHash(requestInfo, hasAccessHash);
        }

        if (data is IAccessHashOwner o)
        {
            foreach (var item in o.GetAccessHashes())
            {
                UpdateAccessHash(requestInfo, item);
            }
        }

        if (data is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                UpdateAccessHashIfNeeded(requestInfo, item);
            }
        }
    }

    private void UpdateAccessHash(RequestInfo requestInfo, IHasAccessHash hasAccessHash)
    {
        hasAccessHash.AccessHash = accessHashHelper2.GenerateAccessHash(requestInfo.UserId,
            requestInfo.AccessHashKeyId, hasAccessHash.Id, (AccessHashType)hasAccessHash.AccessHashType2);

        //Console.WriteLine($"Update access hash:UserId:{requestInfo.UserId} accessHashKeyId:{requestInfo.AccessHashKeyId} Id:{hasAccessHash.Id} {hasAccessHash.AccessHashType2} {hasAccessHash.AccessHash}");
    }
}
