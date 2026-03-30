using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Change default emoji reaction to use in the quick reaction menu.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setDefaultReaction"/> </c></para>
/// </summary>
internal sealed class SetDefaultReactionHandler(ICommandBus commandBus) 
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetDefaultReaction, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetDefaultReaction obj)
    {
        var value = obj.Reaction switch
        {
            TReactionEmoji emoji => emoji.Emoticon,
            TReactionCustomEmoji custom => $"custom:{custom.DocumentId}",
            _ => "👍"
        };
        var key = ((int)UserConfigType.DefaultReaction).ToString();
        var command = new UpdateUserConfigCommand(UserConfigId.Create(input.UserId, key), input.ToRequestInfo(), input.UserId, key, value);
        await commandBus.PublishAsync(command);
        return new TBoolTrue();
    }
}
