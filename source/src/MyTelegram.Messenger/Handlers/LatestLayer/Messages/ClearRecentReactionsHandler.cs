using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class ClearRecentReactionsHandler(ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestClearRecentReactions, IBool>
{
    private const string RecentKey = "recent_reactions";

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestClearRecentReactions obj)
    {
        var configId = UserConfigId.Create(input.UserId, RecentKey);
        await commandBus.PublishAsync(new UpdateUserConfigCommand(configId, input.ToRequestInfo(), input.UserId, RecentKey, ""));
        return new TBoolTrue();
    }
}
