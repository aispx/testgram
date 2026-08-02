using MyTelegram.Domain.Aggregates.Updates;

namespace MyTelegram.Messenger.Services.SecretChat;

public class SecretChatUpdateDispatcher(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator) : ISecretChatUpdateDispatcher, ITransientDependency
{
    public async Task PushToAllDevicesAsync(long userId,
        IUpdate update,
        long? excludeAuthKeyId = null,
        PushData? pushData = null)
    {
        var updateShort = Wrap(update);

        await SaveForDifferenceAsync(userId, updateShort, excludeAuthKeyId, onlySendToThisAuthKeyId: null);

        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId),
            updateShort,
            excludeAuthKeyId: excludeAuthKeyId,
            pushData: pushData);
    }

    public async Task PushToDeviceAsync(long userId,
        long permAuthKeyId,
        IUpdate update,
        int? qts = null,
        PushData? pushData = null)
    {
        var updateShort = Wrap(update);

        // updateNewEncryptedMessage is recovered from the qts box (encrypted_messages), so it must NOT
        // also go into the generic updates box — it would be delivered twice after a gap.
        if (qts == null)
        {
            await SaveForDifferenceAsync(userId, updateShort, excludeAuthKeyId: null,
                onlySendToThisAuthKeyId: permAuthKeyId);
        }

        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId),
            updateShort,
            onlySendToThisAuthKeyId: permAuthKeyId,
            qts: qts,
            pushData: pushData);
    }

    /// <summary>
    /// Persists the update so updates.getDifference can replay it. Without this an offline device
    /// never learns that a secret chat was requested, accepted or discarded: the live push is only
    /// enqueued for connected sessions, and only updateNewEncryptedMessage has a durable qts box.
    /// <para>
    /// Rows are stamped <see cref="UpdatesType.EncryptedUpdates"/> and carry <c>pts = 0</c>, matching
    /// upstream where <c>updateEncryption</c> has no pts. They are replayed by
    /// <c>GetUpdatesByGlobalSeqNoQuery</c>, which is scoped to this marker and to the caller's device —
    /// deliberately NOT by the shared pts box, whose <c>Pts &gt; MinPts</c> filter drops every
    /// <c>pts = 0</c> row and whose readers cannot honour <c>OnlySendToThisAuthKeyId</c>.
    /// </para>
    /// </summary>
    private async Task SaveForDifferenceAsync(long ownerPeerId,
        TUpdateShort updateShort,
        long? excludeAuthKeyId,
        long? onlySendToThisAuthKeyId)
    {
        var globalSeqNo = await idGenerator.NextLongIdAsync(IdType.GlobalSeqNo);

        await commandBus.PublishAsync(new CreateUpdatesCommand(
                UpdatesId.New,
                ownerPeerId,
                excludeAuthKeyId,
                null,
                null,
                onlySendToThisAuthKeyId,
                UpdatesType.EncryptedUpdates,
                0,
                null,
                updateShort.Date,
                globalSeqNo,
                [updateShort.Update],
                null,
                null),
            default);
    }

    private static TUpdateShort Wrap(IUpdate update)
    {
        return new TUpdateShort
        {
            Update = update,
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }
}
