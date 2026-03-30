using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetRecentReactionsHandler(IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetRecentReactions, MyTelegram.Schema.Messages.IReactions>
{
    private const string RecentKey = "recent_reactions";

    private static readonly IReadOnlyList<IReaction> DefaultRecent =
        GetAvailableReactionsHandler.DefaultReactions
            .Cast<TAvailableReaction>()
            .Where(r => !r.Inactive && !r.Premium)
            .Take(8)
            .Select(r => (IReaction)new TReactionEmoji { Emoticon = r.Reaction })
            .ToList();

    protected override async Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetRecentReactions obj)
    {
        var limit = obj.Limit > 0 ? obj.Limit : 8;
        var config = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, RecentKey));

        IReadOnlyList<IReaction> reactions;
        if (config?.Value is { Length: > 0 })
        {
            reactions = config.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Take(limit)
                .Select(e => (IReaction)new TReactionEmoji { Emoticon = e })
                .ToList();
        }
        else
        {
            reactions = DefaultRecent.Take(limit).ToList();
        }

        return new TReactions { Hash = 0, Reactions = new TVector<IReaction>(reactions.ToList()) };
    }
}
