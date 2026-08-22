namespace MyTelegram.Messenger.Converters.ConverterServices.Messages;

internal sealed class GetHistoryConverterService(IUserConverterService userConverterService, IChatConverterService chatConverterService,
    IMessageConverterService messageConverterService,
    IMinConstructorReducer minConstructorReducer
    ) : IGetHistoryConverterService, ITransientDependency
{
    public IMessages ToMessages(IRequestWithAccessHashKeyId request, GetMessageOutput output, int layer)
    {
        var messages = messageConverterService.ToMessageList(output.SelfUserId,
            output.MessageList,
            output.PollList,
            output.ChosenPollOptions,
            output.UserReactionList,
            layer);

        var users = userConverterService.ToUserList(request,
            output.UserList,
            output.PhotoList,
            output.ContactList,
            output.PrivacyList,
            layer);

        var channels = chatConverterService.ToChannelList(request,
            output.ChannelList,
            output.PhotoList,
            output.ChannelMemberList,
            output.JoinedChannelIdList,
            layer);

        // Peers the caller only sees because somebody else's message named them go out as min, so they
        // carry no relationship the caller does not have and get addressed through an
        // input*FromMessage citation later. See https://corefork.telegram.org/api/min
        minConstructorReducer.Reduce(request, output.MessageList, users, channels);

        var hasMessages = messages.Count > 0;

        var offsetId = hasMessages ? output.MessageList.Max(p => p.MessageId) : 0;
        if (output.OffsetInfo?.LoadType == LoadType.Backward)
        {
            offsetId = hasMessages ? output.MessageList.Min(p => p.MessageId) : 0;
        }

        if (hasMessages && output.MessageList.All(p => p.ToPeerType == PeerType.Channel) && !output.IsSearchGlobal)
        {
            var channelPts = output.ChannelList.FirstOrDefault()?.Pts ?? output.Pts;

            return new TChannelMessages
            {
                Chats = [.. channels],
                Messages = [.. messages],
                Users = [.. users],
                Pts = channelPts,
                Count = messages.Count,
                OffsetIdOffset = offsetId,
                Topics = [],
                Inexact = false
            };
        }

        if (messages.Count == output.Limit)
        {
            // next_rate is the paging cursor for messages.searchGlobal: the date of the oldest
            // message in this page, which the client sends back as offset_rate.
            // See https://corefork.telegram.org/method/messages.searchGlobal
            var nextRate = output.MessageList.Min(p => p.Date);

            return new TMessagesSlice
            {
                Chats = [.. channels],
                Count = messages.Count,
                Inexact = true,
                NextRate = nextRate,
                Messages = [.. messages],
                Users = [.. users],
                OffsetIdOffset = offsetId,
                Topics = []
            };
        }

        return new TMessages
        {
            Chats = [.. channels],
            Messages = [.. messages],
            Users = [.. users],
            Topics = []
        };
    }
}
