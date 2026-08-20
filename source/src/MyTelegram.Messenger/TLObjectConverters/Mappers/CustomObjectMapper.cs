using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.TLObjectConverters.Mappers;

public class CustomObjectMapper : ILayeredMapper,
    IObjectMapper<IPtsReadModel, TState>,
    IObjectMapper<SearchGlobalInput, GetMessagesQuery>,
    IObjectMapper<SearchInput, GetMessagesQuery>,
    IObjectMapper<GetHistoryInput, GetMessagesQuery>,
    IObjectMapper<GetMessagesInput, GetMessagesQuery>,
    IObjectMapper<GetRepliesInput, GetMessagesQuery>,
                                                  ITransientDependency
{
    public GetMessagesQuery Map(GetHistoryInput source)
    {
        return Map(source, null!);
    }

    public GetMessagesQuery Map(GetHistoryInput source,
        GetMessagesQuery destination)
    {
        return new GetMessagesQuery(source.OwnerPeerId,
            source.MessageType,
            null,
            [],
            source.ChannelHistoryMinId,
            source.Limit,
            null,
            source.Peer,
            source.SelfUserId,
            0,
            FilterSenderUserId: source.FilterSenderUserId,
            SavedPeerId: source.SavedPeerId,
            // max_id/min_id bound the page from both sides when a client fills a hole in the history;
            // dropping them here returned an unrelated page.
            MinId: source.MinId,
            MaxId: source.MaxId,
            GeoLiveOnly: source.GeoLiveOnly);
    }

    public GetMessagesQuery Map(GetMessagesInput source)
    {
        return Map(source, null!);
    }

    public GetMessagesQuery Map(GetMessagesInput source,
        GetMessagesQuery destination)
    {
        return new GetMessagesQuery(source.OwnerPeerId,
            MessageType.Unknown,
            null,
            source.MessageIdList,
            0,
            source.Limit,
            null,
            source.Peer,
            source.SelfUserId,
            0);
    }

    public GetMessagesQuery? Map(GetRepliesInput source)
    {
        return Map(source, null!);
    }

    public GetMessagesQuery? Map(GetRepliesInput source,
        GetMessagesQuery destination)
    {
        return new GetMessagesQuery(source.OwnerPeerId,
            MessageType.Unknown,
            null,
            [],
            0,
            source.Limit,
            null,
            null,
            source.SelfUserId,
            0,
            source.ReplyToMsgId,
            // A thread page is bounded exactly like a history page: messages.getReplies carries
            // max_id/min_id and offset_date. See https://corefork.telegram.org/api/threads
            MinDate: source.MinDate,
            MaxDate: source.MaxDate,
            MinId: source.MinId,
            MaxId: source.MaxId);
    }

    public TState Map(IPtsReadModel source)
    {
        return Map(source, new TState());
    }

    public TState Map(IPtsReadModel source,
        TState destination)
    {
        destination.Date = source.Date;
        destination.Pts = source.Pts;
        destination.Qts = source.Qts;
        destination.UnreadCount = source.UnreadCount;
        // Clients reject a state whose seq moved backwards and immediately re-issue
        // updates.getDifference, so leaving this at 0 spins them in a tight sync loop. Rows written
        // before Seq existed have no value stored, hence the floor of 1.
        destination.Seq = Math.Max(source.Seq, 1);

        return destination;
    }

    public GetMessagesQuery Map(SearchGlobalInput source)
    {
        return Map(source, null!);
    }

    public GetMessagesQuery Map(SearchGlobalInput source,
        GetMessagesQuery destination)
    {
        return new GetMessagesQuery(source.OwnerPeerId,
                source.MessageType,
                source.Q,
                [],
                0,
                source.Limit,
                null,
                null,
                source.SelfUserId,
                0,
                BroadcastsOnly: source.BroadcastsOnly,
                GroupsOnly: source.GroupsOnly,
                UsersOnly: source.UsersOnly,
                Tokens: source.Tokens,
                MessageTypes: source.MessageTypes,
                MinDate: source.MinDate,
                MaxDate: source.MaxDate,
                MinId: source.MinId,
                MaxId: source.MaxId,
                OffsetRate: source.OffsetRate
            )
        {
            IsSearchGlobal = source.IsSearchGlobal,
            JoinedChannelIdList = source.JoinedChannelList
        };
    }

    public GetMessagesQuery Map(SearchInput source)
    {
        return Map(source, null!);
    }

    public GetMessagesQuery Map(SearchInput source,
        GetMessagesQuery destination)
    {
        return new GetMessagesQuery(source.OwnerPeerId,
            source.MessageType,
            source.Q,
            [],
            0,
            source.Limit,
            null,
            source.Peer,
            source.SelfUserId,
            0,
            ReplyToMsgId: source.TopMsgId,
            Tokens: source.Tokens,
            FilterSenderUserId: source.FilterSenderUserId,
            SavedPeerId: source.SavedPeerId,
            MessageTypes: source.MessageTypes,
            MinDate: source.MinDate,
            MaxDate: source.MaxDate,
            MinId: source.MinId,
            MaxId: source.MaxId
            );
    }
}
