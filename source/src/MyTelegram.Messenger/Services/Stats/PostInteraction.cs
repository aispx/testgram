namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// Interaction counters for a single recent post or story, used to build
/// <c>recent_posts_interactions</c> (a list of <c>PostInteractionCounters</c>).
/// </summary>
/// <param name="Type">Whether the item is a <see cref="StatsEntityType.Message"/> or <see cref="StatsEntityType.Story"/>.</param>
/// <param name="ItemId">The message id or story id.</param>
/// <param name="Date">The post/story date (Unix seconds) used to order newest-first.</param>
/// <param name="Views">Number of views.</param>
/// <param name="Forwards">Number of forwards/reposts to public chats and channels.</param>
/// <param name="Reactions">Number of reactions.</param>
public readonly record struct PostInteraction(
    StatsEntityType Type,
    int ItemId,
    int Date,
    int Views,
    int Forwards,
    int Reactions);
