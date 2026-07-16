using MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// The Stats_Service: computes statistics result objects (broadcast, megagroup, message, story,
/// public forwards) and resolves async graphs from the storage components and the Graph_Builder.
/// </summary>
public interface IStatsService
{
    Task<IBroadcastStats> GetBroadcastStatsAsync(IRequestInput input, long channelId, bool dark);
    Task<IMegagroupStats> GetMegagroupStatsAsync(IRequestInput input, long channelId, bool dark);
    Task<IMessageStats> GetMessageStatsAsync(IRequestInput input, long channelId, int msgId, bool dark);
    Task<IStoryStats> GetStoryStatsAsync(IRequestInput input, Peer peer, int storyId, bool dark);
    Task<IPublicForwards> GetMessagePublicForwardsAsync(IRequestInput input, long channelId, int msgId, string offset, int limit);
    Task<IPublicForwards> GetStoryPublicForwardsAsync(IRequestInput input, Peer peer, int storyId, string offset, int limit);
    Task<IStatsGraph> LoadAsyncGraphAsync(IRequestInput input, string token, long? x);
}
