namespace MyTelegram.Messenger.Services.Interfaces;

public interface IMessageAppService
{
    void CheckBotPermission(long requestUserId, Peer toPeer);

    Task CheckSendAsAsync(long requestUserId, Peer toPeer, Peer? sendAs);

    /// <summary>
    /// Returns the channel peer to attribute an action to when the user acts as an anonymous admin
    /// of that channel, or <c>null</c> when the action should be attributed to the user themselves.
    /// </summary>
    Task<Peer?> GetAnonymousSendAsPeerAsync(long channelId, long userId);

    Task<GetMessageOutput> GetChannelDifferenceAsync(GetDifferenceInput input);
    Task<GetMessageOutput> GetDifferenceAsync(GetDifferenceInput input);
    Task<GetMessageOutput> GetHistoryAsync(GetHistoryInput input);
    Task<GetMessageOutput> GetMessagesAsync(GetMessagesInput input);
    Task<GetMessageOutput> GetRepliesAsync(GetRepliesInput input);

    Task<GetMessageOutput> SearchAsync(SearchInput input);
    Task<GetMessageOutput> SearchGlobalAsync(SearchGlobalInput input);
    Task SendMessageAsync(List<SendMessageInput> inputs);
    Task<SearchPostsResult> SearchPostsAsync(long selfUserId, SearchPostsQuery searchPostsQuery);
    (HashSet<long> userIds, HashSet<long> channelIds) GetExtraPeerIds(
        IReadOnlyCollection<IMessageReadModel> messageReadModels);

    Task<bool> CanSendAsPeerAsync(long channelId, long userId);
    /// <summary>
    /// Validates, normalises and autolinks the entities of a text.
    /// See https://corefork.telegram.org/api/entities
    /// </summary>
    Task<MessageEntityProcessingResult> ProcessMessageEntitiesAsync(
        string? message,
        IEnumerable<IMessageEntity>? entities,
        Peer toPeer);
    List<string> GetHashtags(string? message);
    Task<bool> IsValidSendAsPeerAsync(long requestUserId, Peer toPeer, Peer? sendAsPeer);
}