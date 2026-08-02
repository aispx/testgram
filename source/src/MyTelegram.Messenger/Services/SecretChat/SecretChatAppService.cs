using MyTelegram.Domain.Aggregates.EncryptedChat;
using MyTelegram.Services.Phone;

namespace MyTelegram.Messenger.Services.SecretChat;

public class SecretChatAppService(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IIdGenerator idGenerator,
    IBlockCacheAppService blockCacheAppService,
    ISecretChatAccessResolver accessResolver,
    ISecretChatUpdateDispatcher updateDispatcher,
    ISecretChatMessageStore messageStore,
    ISecretChatRequestLedger requestLedger,
    IEncryptedFileStore encryptedFileStore,
    ILayeredService<IEncryptedChatConverter> encryptedChatLayeredService,
    ILayeredService<IEncryptedMessageConverter> encryptedMessageLayeredService,
    ILayeredService<IEncryptedFileConverter> encryptedFileLayeredService) : ISecretChatAppService, ITransientDependency
{
    /// <summary>
    /// Upper bound on an encrypted payload. Keeps a single message comfortably inside MongoDB's 16 MB
    /// document limit so an oversized blob surfaces as DATA_TOO_LONG instead of a write failure.
    /// </summary>
    private const int MaxEncryptedPayloadLength = 8 * 1024 * 1024;

    private static int CurrentDate => DateTime.UtcNow.ToTimestamp();

    public async Task<IEncryptedChat> RequestEncryptionAsync(IRequestInput input,
        IInputUser userId,
        int randomId,
        byte[] ga)
    {
        await accessResolver.EnsureUserCallerAsync(input);

        // Resolve the target user. Secret chats cannot target self or bots.
        var targetUserId = userId switch
        {
            TInputUser inputUser => inputUser.UserId,
            _ => 0
        };

        if (targetUserId == 0 || targetUserId == input.UserId)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var targetUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(targetUserId));
        if (targetUser == null || targetUser.Bot)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (targetUser!.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.InputUserDeactivated.ThrowRpcError();
        }

        if (await blockCacheAppService.IsBlockedAsync(targetUserId, input.UserId))
        {
            RpcErrors.RpcErrors403.UserIsBlocked.ThrowRpcError();
        }

        // The server MUST validate the DH range of g_a (public value; not a confidentiality breach).
        if (!PhoneCallDhValidator.IsValidDhValue(ga))
        {
            RpcErrors.RpcErrors400.DhGAInvalid.ThrowRpcError();
        }

        var chatConverter = encryptedChatLayeredService.GetConverter(input.Layer);

        // Idempotency by (adminId, random_id): a retry returns the previously created chat.
        var existing = await requestLedger.FindAsync(input.UserId, randomId);
        if (existing != null)
        {
            return chatConverter.ToEncryptedChatWaiting(existing.ChatId,
                existing.AccessHash,
                existing.Date,
                input.UserId,
                existing.ParticipantId);
        }

        var chatId = await idGenerator.NextIdAsync(IdType.SecretChatId, 0);
        var accessHash = NewNonZeroId();
        var date = CurrentDate;

        var reserved = await requestLedger.ReserveAsync(new SecretChatRequestDocument
        {
            Id = SecretChatRequestDocument.BuildId(input.UserId, randomId),
            AdminId = input.UserId,
            RandomId = randomId,
            ChatId = chatId,
            AccessHash = accessHash,
            ParticipantId = targetUserId,
            Date = date
        });

        if (reserved.ChatId != chatId)
        {
            // Lost a concurrent race for the same (adminId, random_id): return the winner's chat.
            return chatConverter.ToEncryptedChatWaiting(reserved.ChatId,
                reserved.AccessHash,
                reserved.Date,
                input.UserId,
                reserved.ParticipantId);
        }

        await commandBus.PublishAsync(new CreateEncryptedChatCommand(EncryptedChatId.Create(chatId),
                chatId,
                input.UserId,
                targetUserId,
                input.PermAuthKeyId,
                accessHash,
                ga,
                randomId,
                date),
            default);

        // encryptedChatRequested goes to ALL of the participant's devices — none is bound yet.
        var requestedChat = chatConverter.ToEncryptedChatRequested(chatId, accessHash, date, input.UserId,
            targetUserId, ga);
        await updateDispatcher.PushToAllDevicesAsync(targetUserId,
            chatConverter.ToUpdateEncryption(requestedChat, date),
            pushData: CreatePushData(PushNotificationTypes.EncryptionRequest, targetUserId, chatId));

        return chatConverter.ToEncryptedChatWaiting(chatId, accessHash, date, input.UserId, targetUserId);
    }

    public async Task<IEncryptedChat> AcceptEncryptionAsync(IRequestInput input,
        IInputEncryptedChat peer,
        byte[] gb,
        long keyFingerprint)
    {
        var access = await accessResolver.ResolveAsync(input, peer);
        var chat = access.Chat;

        if (chat.ChatState == ChatState.Discarded)
        {
            RpcErrors.RpcErrors400.EncryptionAlreadyDeclined.ThrowRpcError();
        }

        if (chat.ChatState == ChatState.Active)
        {
            RpcErrors.RpcErrors400.EncryptionAlreadyAccepted.ThrowRpcError();
        }

        if (access.CallerIsAdmin)
        {
            // The requesting party cannot accept its own request.
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        if (!PhoneCallDhValidator.IsValidDhValue(gb))
        {
            RpcErrors.RpcErrors400.DhGAInvalid.ThrowRpcError();
        }

        // The aggregate re-validates the state transition and wins any accept/discard race.
        await commandBus.PublishAsync(new AcceptEncryptedChatCommand(EncryptedChatId.Create((int)chat.ChatId),
                input.UserId,
                input.PermAuthKeyId,
                gb,
                keyFingerprint,
                CurrentDate),
            default);

        var chatConverter = encryptedChatLayeredService.GetConverter(input.Layer);

        // The admin's bound device gets encryptedChat with g_b (the admin needs the participant's value).
        var chatForAdmin = chatConverter.ToEncryptedChat(chat.ChatId, chat.AccessHash, chat.Date, chat.AdminId,
            chat.ParticipantId, gb, keyFingerprint);
        await updateDispatcher.PushToDeviceAsync(chat.AdminId,
            chat.AdminPermAuthKeyId,
            chatConverter.ToUpdateEncryption(chatForAdmin, CurrentDate),
            pushData: CreatePushData(PushNotificationTypes.EncryptionAccept, chat.AdminId, chat.ChatId));

        // The participant's OTHER devices drop the pending request.
        var discardedForOtherDevices = chatConverter.ToEncryptedChatDiscarded(chat.ChatId, historyDeleted: false);
        await updateDispatcher.PushToAllDevicesAsync(input.UserId,
            chatConverter.ToUpdateEncryption(discardedForOtherDevices, CurrentDate),
            excludeAuthKeyId: input.PermAuthKeyId);

        // The caller is the participant: g_a_or_b is the admin's g_a.
        return chatConverter.ToEncryptedChat(chat.ChatId, chat.AccessHash, chat.Date, chat.AdminId,
            chat.ParticipantId, chat.Ga, keyFingerprint);
    }

    public async Task<IBool> DiscardEncryptionAsync(IRequestInput input, int chatId, bool deleteHistory)
    {
        if (chatId == 0)
        {
            RpcErrors.RpcErrors400.ChatIdEmpty.ThrowRpcError();
        }

        var access = await accessResolver.ResolveByChatIdAsync(input, chatId);
        var chat = access.Chat;

        if (chat.ChatState == ChatState.Discarded)
        {
            RpcErrors.RpcErrors400.EncryptionAlreadyDeclined.ThrowRpcError();
        }

        await commandBus.PublishAsync(new DiscardEncryptedChatCommand(EncryptedChatId.Create(chatId),
                input.UserId,
                deleteHistory,
                CurrentDate),
            default);

        if (deleteHistory)
        {
            // Server-side half of delete_history: drop stored blobs and their unacked box rows.
            await messageStore.DeleteByChatAsync(chatId);
        }

        var chatConverter = encryptedChatLayeredService.GetConverter(input.Layer);
        var discarded = chatConverter.ToEncryptedChatDiscarded(chatId, deleteHistory);
        var update = chatConverter.ToUpdateEncryption(discarded, CurrentDate);

        // All devices of the other party + the caller's other devices.
        await updateDispatcher.PushToAllDevicesAsync(access.OtherUserId, update);
        await updateDispatcher.PushToAllDevicesAsync(input.UserId, update,
            excludeAuthKeyId: input.PermAuthKeyId);

        return new TBoolTrue();
    }

    public Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data,
        bool silent)
    {
        return SendEncryptedCoreAsync(input, peer, randomId, data, SendMessageType.Text, null, silent);
    }

    public async Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedFileAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data,
        IInputEncryptedFile file,
        bool silent)
    {
        return await SendEncryptedCoreAsync(input, peer, randomId, data, SendMessageType.Media, file, silent);
    }

    public Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedServiceAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data)
    {
        // Service messages are protocol-level (read receipts, TTL changes, resend requests): they must
        // never raise a user-visible push notification.
        return SendEncryptedCoreAsync(input, peer, randomId, data, SendMessageType.MessageService, null,
            silent: true);
    }

    public async Task<IBool> ReadEncryptedHistoryAsync(IRequestInput input, IInputEncryptedChat peer, int maxDate)
    {
        var access = await accessResolver.ResolveAsync(input, peer);
        accessResolver.RequireActive(access, forSend: false);

        var messageConverter = encryptedMessageLayeredService.GetConverter(input.Layer);
        await updateDispatcher.PushToDeviceAsync(access.OtherUserId,
            access.OtherPermAuthKeyId,
            messageConverter.ToUpdateEncryptedMessagesRead(access.Chat.ChatId, maxDate, CurrentDate));

        return new TBoolTrue();
    }

    public async Task<IBool> SetEncryptedTypingAsync(IRequestInput input, IInputEncryptedChat peer, bool typing)
    {
        var access = await accessResolver.ResolveAsync(input, peer);
        accessResolver.RequireActive(access, forSend: false);

        if (typing)
        {
            // Delivered to the other participant's bound device only, never back to the caller.
            var messageConverter = encryptedMessageLayeredService.GetConverter(input.Layer);
            await updateDispatcher.PushToDeviceAsync(access.OtherUserId,
                access.OtherPermAuthKeyId,
                messageConverter.ToUpdateEncryptedChatTyping(access.Chat.ChatId));
        }

        return new TBoolTrue();
    }

    public async Task<IEncryptedFile> UploadEncryptedFileAsync(IRequestInput input,
        IInputEncryptedChat peer,
        IInputEncryptedFile file)
    {
        var access = await accessResolver.ResolveAsync(input, peer);
        accessResolver.RequireActive(access, forSend: true);

        var descriptor = await ResolveInputFileAsync(input.UserId, file);
        if (descriptor == null)
        {
            RpcErrors.RpcErrors400.FileEmtpy.ThrowRpcError();
        }

        return encryptedFileLayeredService.GetConverter(input.Layer).ToEncryptedFile(descriptor);
    }

    public async Task<TVector<long>> ReceivedQueueAsync(IRequestInput input, int maxQts)
    {
        await accessResolver.EnsureUserCallerAsync(input);

        // Validated against the ASSIGNED high-water, not the delivered watermark: a client can already
        // hold a live-pushed qts whose predecessor is still in flight, and rejecting that ack would be a
        // spurious MAX_QTS_INVALID. The requirement is worded the same way ("highest qts assigned to the
        // calling Authorization_Key"). AckAsync only touches rows with Qts > 0, so nothing unassigned acks.
        var assignedQts = await messageStore.GetAssignedQtsAsync(input.UserId, input.PermAuthKeyId);
        if (maxQts > assignedQts)
        {
            // A fresh Authorization_Key has assigned == QtsInitialValue - 1.
            RpcErrors.RpcErrors400.MaxQtsInvalid.ThrowRpcError();
        }

        var ackedRandomIds = await messageStore.AckAsync(input.UserId, input.PermAuthKeyId, maxQts);

        return new TVector<long>(ackedRandomIds);
    }

    public async Task<IBool> ReportEncryptedSpamAsync(IRequestInput input, IInputEncryptedChat peer)
    {
        var access = await accessResolver.ResolveAsync(input, peer);

        // At most one report per (caller, chat); the aggregate makes repeats a no-op.
        await commandBus.PublishAsync(
            new ReportEncryptedChatSpamCommand(EncryptedChatId.Create((int)access.Chat.ChatId), input.UserId),
            default);

        return new TBoolTrue();
    }

    private async Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> SendEncryptedCoreAsync(IRequestInput input,
        IInputEncryptedChat peer,
        long randomId,
        ReadOnlyMemory<byte> data,
        SendMessageType messageType,
        IInputEncryptedFile? inputFile,
        bool silent)
    {
        var access = await accessResolver.ResolveAsync(input, peer);
        accessResolver.RequireActive(access, forSend: true);

        if (messageType == SendMessageType.MessageService)
        {
            // Checked AFTER the state check (ENCRYPTION_DECLINED wins over USER_DELETED).
            var otherUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(access.OtherUserId));
            if (otherUser?.IsDeleted == true)
            {
                RpcErrors.RpcErrors403.UserDeleted.ThrowRpcError();
            }
        }

        // The only seq_no-adjacent check a blind relay can make: the outer envelope must at least be
        // large enough to hold key_fingerprint + msg_key + one AES block. Anything shorter cannot be
        // decrypted by any client, so relaying it would burn a qts on a message the recipient can only
        // read as a gap. Divisibility by 16 is deliberately NOT enforced — TDLib truncates a ragged
        // tail rather than rejecting it, so a stricter rule here could reject what upstream relays.
        if (data.Length < SecretChatConsts.MinEncryptedPayloadLength)
        {
            RpcErrors.RpcErrors400.DataInvalid.ThrowRpcError();
        }

        if (data.Length > MaxEncryptedPayloadLength)
        {
            RpcErrors.RpcErrors400.DataTooLong.ThrowRpcError();
        }

        var fileConverter = encryptedFileLayeredService.GetConverter(input.Layer);
        var messageConverter = encryptedMessageLayeredService.GetConverter(input.Layer);
        var chatId = access.Chat.ChatId;

        // Idempotency by (chatId, sender, random_id): return the original date, no re-store, no re-push.
        var existing = await messageStore.FindAsync(chatId, input.UserId, randomId);
        if (existing != null)
        {
            if (existing.Qts != 0)
            {
                return BuildSentResult(existing, fileConverter);
            }

            // The previous attempt stored the row but died before assigning a qts, so the message is
            // invisible to both getDifference and receivedQueue. Finish the interrupted delivery instead
            // of reporting a success that would never arrive.
            return await AssignQtsAndDispatchAsync(input, access, existing, messageConverter, fileConverter,
                silent);
        }

        IEncryptedFile? encryptedFile = null;
        if (messageType == SendMessageType.Media)
        {
            var descriptor = await ResolveInputFileAsync(input.UserId, inputFile);
            if (descriptor == null)
            {
                RpcErrors.RpcErrors400.FileEmtpy.ThrowRpcError();
            }

            encryptedFile = fileConverter.ToEncryptedFile(descriptor);
        }

        var document = new EncryptedMessageDocument
        {
            Id = EncryptedMessageDocument.BuildId(chatId, input.UserId, randomId),
            ChatId = chatId,
            UserId = input.UserId,
            PermAuthKeyId = input.PermAuthKeyId,
            RecipientUserId = access.OtherUserId,
            RecipientPermAuthKeyId = access.OtherPermAuthKeyId,
            Data = data.ToArray(),
            File = encryptedFile?.ToBytes(),
            Date = CurrentDate,
            CreatedAt = DateTime.UtcNow,
            MessageType = messageType,
            RandomId = randomId
        };

        var storeResult = await messageStore.StoreAsync(document);
        if (!storeResult.IsNew)
        {
            var stored = storeResult.Stored;

            return stored.Qts != 0
                ? BuildSentResult(stored, fileConverter)
                : await AssignQtsAndDispatchAsync(input, access, stored, messageConverter, fileConverter, silent);
        }

        return await AssignQtsAndDispatchAsync(input, access, document, messageConverter, fileConverter, silent);
    }

    /// <summary>
    /// Completes a stored-but-unsequenced message: allocate the recipient's qts, make the row visible,
    /// then push. The order (insert -> allocate -> set -> push) matters — allocating before the insert
    /// would burn a qts on a duplicate-key race and punch a permanent gap in the recipient's sequence.
    /// <para>
    /// The ordering alone is not what keeps the recipient's watermark honest: the allocation is registered
    /// as in-flight by <see cref="ISecretChatMessageStore.AllocateQtsAsync"/> and only released by
    /// <c>SetQtsAsync</c>, so a concurrent send to the same device cannot advertise a qts over this one
    /// while the row is still unwritten. Anything that throws in between must release it explicitly.
    /// </para>
    /// </summary>
    private async Task<MyTelegram.Schema.Messages.ISentEncryptedMessage> AssignQtsAndDispatchAsync(
        IRequestInput input,
        SecretChatAccess access,
        EncryptedMessageDocument document,
        IEncryptedMessageConverter messageConverter,
        IEncryptedFileConverter fileConverter,
        bool silent)
    {
        var qts = await messageStore.AllocateQtsAsync(document.RecipientUserId, document.RecipientPermAuthKeyId);

        bool sequenced;
        try
        {
            sequenced = await messageStore.SetQtsAsync(document.Id, qts, document.RecipientUserId,
                document.RecipientPermAuthKeyId);
        }
        catch
        {
            // Release the allocation rather than letting the staleness cut do it: an entry left behind
            // holds this device's watermark down for the whole window.
            await messageStore.AbandonQtsAsync(qts, document.RecipientUserId, document.RecipientPermAuthKeyId);

            throw;
        }

        if (!sequenced)
        {
            // A concurrent request already sequenced and pushed this row. Report its qts, do not push again.
            var stored = await messageStore.FindAsync(document.ChatId, document.UserId, document.RandomId);

            return BuildSentResult(stored ?? document, fileConverter);
        }

        document.Qts = qts;

        var encryptedFile = DeserializeFile(document);
        var encryptedMessage = document.MessageType == SendMessageType.MessageService
            ? messageConverter.ToEncryptedMessageService(document)
            : messageConverter.ToEncryptedMessage(document, encryptedFile);

        await updateDispatcher.PushToDeviceAsync(document.RecipientUserId,
            document.RecipientPermAuthKeyId,
            messageConverter.ToUpdateNewEncryptedMessage(encryptedMessage, qts),
            qts: qts,
            pushData: silent
                ? null
                : CreatePushData(PushNotificationTypes.EncryptedMessage, document.RecipientUserId,
                    document.ChatId, document.RandomId));

        return BuildSentResult(document, fileConverter);
    }

    /// <summary>Deserializes the stored TL file descriptor, tolerating an empty or malformed blob.</summary>
    private static IEncryptedFile? DeserializeFile(EncryptedMessageDocument document)
    {
        if (document.File is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return ((ReadOnlyMemory<byte>)document.File).ToTObject<IEncryptedFile>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<EncryptedFileDescriptor?> ResolveInputFileAsync(long userId, IInputEncryptedFile? inputFile)
    {
        switch (inputFile)
        {
            case TInputEncryptedFileUploaded uploaded:
                return await encryptedFileStore.StoreUploadedAsync(userId,
                    uploaded.Id,
                    uploaded.Parts,
                    uploaded.KeyFingerprint,
                    uploaded.Md5Checksum);
            case TInputEncryptedFileBigUploaded bigUploaded:
                return await encryptedFileStore.StoreUploadedAsync(userId,
                    bigUploaded.Id,
                    bigUploaded.Parts,
                    bigUploaded.KeyFingerprint,
                    md5Checksum: null);
            case TInputEncryptedFile existingFile:
                return await encryptedFileStore.ResolveAsync(existingFile.Id, existingFile.AccessHash);
            default:
                return null;
        }
    }

    private static MyTelegram.Schema.Messages.ISentEncryptedMessage BuildSentResult(
        EncryptedMessageDocument document,
        IEncryptedFileConverter fileConverter)
    {
        if (document.MessageType == SendMessageType.Media)
        {
            // encryptedFile is non-optional on sentEncryptedFile: an empty or unreadable stored
            // descriptor must degrade to encryptedFileEmpty, never to null.
            return new TSentEncryptedFile
            {
                Date = document.Date,
                File = DeserializeFile(document) ?? fileConverter.ToEncryptedFile(descriptor: null)
            };
        }

        return new TSentEncryptedMessage
        {
            Date = document.Date
        };
    }

    private static PushData CreatePushData(string locKey, long toUserId, long encryptionId, long? randomId = null)
    {
        return new PushData(locKey,
            [],
            toUserId,
            new PushNotificationCustomData
            {
                EncryptionId = encryptionId,
                RandomId = randomId
            },
            null);
    }

    /// <summary>
    ///     Generates a secret chat <c>access_hash</c>. chat_id is sequential and therefore guessable, so this
    ///     value is the only per-chat secret and must come from a CSPRNG rather than <see cref="Random.Shared" />,
    ///     whose xoshiro256** state is recoverable from a handful of observed outputs.
    /// </summary>
    private static long NewNonZeroId()
    {
        while (true)
        {
            var id = BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
            if (id != 0)
            {
                return id;
            }
        }
    }
}
