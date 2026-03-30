namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetTopReactionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetTopReactions, MyTelegram.Schema.Messages.IReactions>
{
    private static readonly IReadOnlyList<IReaction> TopReactions =
        GetAvailableReactionsHandler.DefaultReactions
            .Cast<TAvailableReaction>()
            .Where(r => !r.Inactive && !r.Premium)
            .Select(r => (IReaction)new TReactionEmoji { Emoticon = r.Reaction })
            .ToList();

    protected override Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetTopReactions obj)
    {
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, TopReactions.Count) : TopReactions.Count;
        return Task.FromResult<IReactions>(new TReactions
        {
            Hash = 0,
            Reactions = new TVector<IReaction>(TopReactions.Take(limit).ToList())
        });
    }
}
