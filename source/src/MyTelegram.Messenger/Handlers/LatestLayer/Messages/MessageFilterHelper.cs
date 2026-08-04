namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Single source of truth for mapping a TL <see cref="IMessagesFilter"/> to the internal
/// <see cref="MessageType"/> values it should match.
/// See https://corefork.telegram.org/api/search#filtering-by-message-type
/// </summary>
/// <remarks>
/// A filter maps to a <em>set</em> of message types rather than a single one, because
/// <c>MediaHelper.GeMessageType</c> classifies every <c>TMessageMediaDocument</c> as
/// <see cref="MessageType.Document"/>: videos, gifs, voice notes and music are all stored
/// as <c>Document</c>. Narrowing those filters to a single type would return nothing, so
/// each media filter also matches <c>Document</c> and therefore yields a wider result set
/// than the official server. Precise classification requires reworking the media pipeline
/// and reindexing existing messages.
/// </remarks>
internal static class MessageFilterHelper
{
    /// <summary>
    /// Message types matching the filter. An empty set means "do not filter by type"
    /// (either no filter, <c>inputMessagesFilterEmpty</c>, or a filter expressed through
    /// a flag instead of a type — see <see cref="IsPinnedFilter"/> and <see cref="IsMyMentionsFilter"/>).
    /// </summary>
    public static List<MessageType> GetMessageTypes(IMessagesFilter? filter)
    {
        return filter switch
        {
            null => [],
            TInputMessagesFilterEmpty => [],
            TInputMessagesFilterPinned => [],
            TInputMessagesFilterMyMentions => [],

            TInputMessagesFilterPhotos => [MessageType.Photo],
            TInputMessagesFilterChatPhotos => [MessageType.Photo],
            TInputMessagesFilterVideo => [MessageType.Video, MessageType.Document],
            TInputMessagesFilterRoundVideo => [MessageType.Video, MessageType.Document],
            TInputMessagesFilterPhotoVideo => [MessageType.Photo, MessageType.Video, MessageType.Document],
            TInputMessagesFilterDocument => [MessageType.Document],
            TInputMessagesFilterGif => [MessageType.Gif, MessageType.Document],
            TInputMessagesFilterVoice => [MessageType.Voice, MessageType.Document],
            TInputMessagesFilterRoundVoice => [MessageType.Voice, MessageType.Document],
            TInputMessagesFilterMusic => [MessageType.Music, MessageType.Document],
            TInputMessagesFilterUrl => [MessageType.Url],
            TInputMessagesFilterPhoneCalls => [MessageType.PhoneCall],
            TInputMessagesFilterGeo => [MessageType.Geo],
            TInputMessagesFilterContacts => [MessageType.Contacts],
            TInputMessagesFilterPoll => [MessageType.Poll],

            _ => ThrowInvalidFilter()
        };
    }

    /// <summary>
    /// <c>inputMessagesFilterPinned</c> selects by the <c>Pinned</c> flag, not by message type.
    /// </summary>
    public static bool IsPinnedFilter(IMessagesFilter? filter)
    {
        return filter is TInputMessagesFilterPinned;
    }

    /// <summary>
    /// <c>inputMessagesFilterMyMentions</c> selects messages mentioning the current user,
    /// which is resolved from message entities rather than a stored message type.
    /// </summary>
    public static bool IsMyMentionsFilter(IMessagesFilter? filter)
    {
        return filter is TInputMessagesFilterMyMentions;
    }

    /// <summary>
    /// <c>messages.getSearchResultsPositions</c> and <c>messages.getSearchResultsCalendar</c>
    /// explicitly reject <c>inputMessagesFilterEmpty</c> and <c>inputMessagesFilterMyMentions</c>.
    /// </summary>
    public static bool IsSupportedByPositionsAndCalendar(IMessagesFilter? filter)
    {
        return filter is not null and not TInputMessagesFilterEmpty and not TInputMessagesFilterMyMentions;
    }

    /// <summary>
    /// Validates the filter, throwing <c>INPUT_FILTER_INVALID</c> for unknown constructors.
    /// </summary>
    public static void EnsureValidFilter(IMessagesFilter? filter)
    {
        GetMessageTypes(filter);
    }

    private static List<MessageType> ThrowInvalidFilter()
    {
        RpcErrors.RpcErrors400.InputFilterInvalid.ThrowRpcError();
        return [];
    }
}
