using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.Converters.ConverterServices;

public class DifferenceConverterService(
    IObjectMapper objectMapper,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService,
    IMessageConverterService messageConverterService,
    IUpdatesResponseService updatesResponseService,
    ILayeredService<IMessageConverter> messageLayeredService) : IDifferenceConverterService, ITransientDependency
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
        IList<IChat> chatListFromUpdates, IReadOnlyCollection<IEncryptedMessageReadModel>? encryptedMessageReadModels, int layer = 0)
    {
        var messageList = messageConverterService.ToMessageList(output.SelfUserId, output.MessageList, output.PollList,
            output.ChosenPollOptions, output.UserReactionList, layer);

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

        if (updateList.Count == limit)
        {
            var differenceSlice = new TDifferenceSlice
            {
                Chats = new TVector<IChat>(channelList ?? []),
                NewEncryptedMessages = new TVector<IEncryptedMessage>(),
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

            return differenceSlice;
        }

        var newEncryptedMessages = Array.Empty<IEncryptedMessage>();

        var difference = new TDifference
        {
            Chats = new TVector<IChat>(channelList ?? []),
            NewEncryptedMessages = new TVector<IEncryptedMessage>(newEncryptedMessages ?? []),
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

        return difference;
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