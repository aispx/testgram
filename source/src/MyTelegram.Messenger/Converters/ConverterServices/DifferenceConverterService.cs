using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.Converters.ConverterServices;

public class DifferenceConverterService(
    IObjectMapper objectMapper,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService,
    IMessageConverterService messageConverterService,
    IUpdatesResponseService updatesResponseService,
    ILayeredService<IMessageConverter> messageLayeredService,
    ILayeredService<IEncryptedMessageConverter> encryptedMessageLayeredService) : IDifferenceConverterService, ITransientDependency
{
    public IChannelDifference ToChannelDifference(
        IRequestWithAccessHashKeyId request,
        GetMessageOutput output, bool isChannelMember, IList<IUpdate> updatesList,
        int updatesMaxPts = 0,
        bool resetLeftToFalse = false,
        int timeoutSeconds = 30,
        int layer = 0)
    {
        var timeout = timeoutSeconds;
        if (output.MessageList.Count == 0 && updatesList.Count == 0)
        {
            return new TChannelDifferenceEmpty { Final = true, Pts = output.Pts, Timeout = timeout };
        }

        var maxPts = updatesMaxPts;
        if (output.MessageList.Count > 0)
        {
            var boxMaxPts = output.MessageList.Max(p => p.Pts);
            maxPts = Math.Max(updatesMaxPts, boxMaxPts);
        }

        var messageList = messageConverterService.ToMessageList(output.SelfUserId, output.MessageList, output.PollList, output.ChosenPollOptions, output.UserReactionList, layer);

        // Filter out null messages and TMessageService with null Action
        messageList = messageList.Where(m => m != null && !(m is TMessageService ms && ms.Action == null)).ToList();

        var channelList = chatConverterService.ToChannelList(request, output.ChannelList, output.PhotoList,
            output.ChannelMemberList, output.JoinedChannelIdList, layer);
        var userList = userConverterService.ToUserList(request, output.UserList, output.PhotoList,
            output.ContactList, output.PrivacyList, layer);

        var layeredUpdates = updatesList.Select(p => updatesResponseService.ToLayeredData(output.SelfUserId, request.AccessHashKeyId, p, layer));

        return new TChannelDifference
        {
            Final = output.Pts == maxPts,
            Pts = maxPts,
            Users = new TVector<IUser>(userList ?? []),
            OtherUpdates = new TVector<IUpdate>(layeredUpdates?.Where(u => u != null) ?? []),
            Timeout = timeout,
            Chats = new TVector<IChat>(channelList ?? []),
            NewMessages = new TVector<IMessage>(messageList ?? [])
        };
    }

    public IDifference ToDifference(
        IRequestWithAccessHashKeyId request,
        GetMessageOutput output, IPtsReadModel? pts, int cachedPts, int limit, IList<IUpdate> updateList,
        IList<IChat> chatListFromUpdates, IReadOnlyCollection<IEncryptedMessageReadModel>? encryptedMessageReadModels, int secretChatQts = 0, bool encryptedMessagesTruncated = false, bool updatesTruncated = false, int layer = 0)
    {
        var messageList = messageConverterService.ToMessageList(output.SelfUserId, output.MessageList, output.PollList,
            output.ChosenPollOptions, output.UserReactionList, layer);

        var newEncryptedMessageList = ToEncryptedMessageList(encryptedMessageReadModels, layer);

        // Filter out null messages and TMessageService with null Action
        messageList = messageList.Where(m => m != null && !(m is TMessageService ms && ms.Action == null)).ToList();
        var userList = userConverterService.ToUserList(request, output.UserList, output.PhotoList,
            output.ContactList, output.PrivacyList, layer);
        var channelList = chatConverterService.ToChannelList(request, output.ChannelList, output.PhotoList,
            output.ChannelMemberList, output.JoinedChannelIdList, layer);

        var qts = pts?.Qts ?? 0;
        var unreadCount = pts?.UnreadCount ?? 0;
        if (unreadCount < 0)
        {
            unreadCount = 0;
        }

        var layeredUpdates = updateList.Select(p => updatesResponseService.ToLayeredData(output.SelfUserId, request.AccessHashKeyId, p, layer));

        // Filter out updates with null messages or TMessageService with null Action
        layeredUpdates = layeredUpdates.Where(u => u != null && !IsInvalidUpdate(u));

        // The slice form tells the client "there is more, ask again". Encrypted messages can be cut off
        // by the same limit independently of the other updates, so they must be able to force it too —
        // and once a second non-encrypted stream (the device-scoped secret-chat handshake replay) is
        // unioned, updateList.Count == limit is unreliable as a truncation signal (overshoot), so the
        // caller passes an explicit flag for it.
        if (updateList.Count == limit || encryptedMessagesTruncated || updatesTruncated)
        {
            var differenceSlice = new TDifferenceSlice
            {
                Chats = new TVector<IChat>(channelList ?? []),
                NewEncryptedMessages = new TVector<IEncryptedMessage>(newEncryptedMessageList),
                NewMessages = new TVector<IMessage>(messageList ?? []),
                OtherUpdates = new TVector<IUpdate>(layeredUpdates?.Where(u => u != null) ?? []),
                Users = new TVector<IUser>(userList ?? []),
                IntermediateState = pts == null
                    ? new TState
                    {
                        Date = DateTime.UtcNow.ToTimestamp(),
                        Qts = qts,
                        Pts = pts?.Pts ?? 1,
                        UnreadCount = unreadCount,
                        Seq = 1
                    }
                    : objectMapper.Map<IPtsReadModel, TState>(pts) ?? new TState
                    {
                        Date = DateTime.UtcNow.ToTimestamp(),
                        Qts = qts,
                        Pts = pts?.Pts ?? 1,
                        UnreadCount = unreadCount,
                        Seq = 1
                    }
            };

            // Only updateNewEncryptedMessage carries qts; reflect the per-Authorization_Key sequence.
            if (secretChatQts > 0 && differenceSlice.IntermediateState is TState sliceState)
            {
                sliceState.Qts = secretChatQts;
            }

            return differenceSlice;
        }

        var difference = new TDifference
        {
            Chats = new TVector<IChat>(channelList ?? []),
            NewEncryptedMessages = new TVector<IEncryptedMessage>(newEncryptedMessageList),
            NewMessages = new TVector<IMessage>(messageList ?? []),
            OtherUpdates = new TVector<IUpdate>(layeredUpdates?.Where(u => u != null) ?? []),
            Users = new TVector<IUser>(userList ?? []),
            State = pts == null
                ? new TState
                {
                    Date = DateTime.UtcNow.ToTimestamp(),
                    Qts = qts,
                    Pts = pts?.Pts ?? 1,
                    UnreadCount = unreadCount,
                    Seq = 1
                }
                : objectMapper.Map<IPtsReadModel, TState>(pts) ?? new TState
                {
                    Date = DateTime.UtcNow.ToTimestamp(),
                    Qts = qts,
                    Pts = pts?.Pts ?? 1,
                    UnreadCount = unreadCount,
                    Seq = 1
                }
        };
        if (cachedPts > pts?.Pts)
        {
            difference.State.Pts = cachedPts;
        }

        // Only updateNewEncryptedMessage carries qts; reflect the per-Authorization_Key sequence.
        if (secretChatQts > 0 && difference.State is TState state)
        {
            state.Qts = secretChatQts;
        }

        return difference;
    }

    private IReadOnlyList<IEncryptedMessage> ToEncryptedMessageList(
        IReadOnlyCollection<IEncryptedMessageReadModel>? encryptedMessageReadModels,
        int layer)
    {
        if (encryptedMessageReadModels == null || encryptedMessageReadModels.Count == 0)
        {
            return [];
        }

        var messageConverter = encryptedMessageLayeredService.GetConverter(layer);

        return encryptedMessageReadModels.Select(m =>
        {
            if (m.MessageType == SendMessageType.MessageService)
            {
                return messageConverter.ToEncryptedMessageService(m);
            }

            // A single unreadable file descriptor must not abort the caller's whole sync: fall back to
            // encryptedFileEmpty (the message body itself is still relayed verbatim).
            IEncryptedFile? file = null;
            if (m.File is { Length: > 0 })
            {
                try
                {
                    file = ((ReadOnlyMemory<byte>)m.File).ToTObject<IEncryptedFile>();
                }
                catch (Exception)
                {
                    file = null;
                }
            }

            return messageConverter.ToEncryptedMessage(m, file);
        }).ToList();
    }

    private static bool IsInvalidUpdate(IUpdate update)
    {
        return update switch
        {
            TUpdateNewChannelMessage { Message: TMessageService ms } when ms.Action == null => true,
            TUpdateNewMessage { Message: TMessageService ms } when ms.Action == null => true,
            TUpdateEditChannelMessage { Message: TMessageService ms } when ms.Action == null => true,
            TUpdateEditMessage { Message: TMessageService ms } when ms.Action == null => true,
            TUpdateNewChannelMessage { Message: null } => true,
            TUpdateNewMessage { Message: null } => true,
            _ => false
        };
    }
}