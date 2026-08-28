using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Dismiss new pending peers recently added to a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.hideChatlistUpdates"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The dismissal has to be remembered on the server: clients call this instead of
/// <c>joinChatlistUpdates</c> when the user wants none of the offered chats, and a dismissal that is not
/// stored means the same chats are offered again on the next poll, every
/// <c>chatlist_update_period</c> seconds.</para>
/// </remarks>
internal sealed class HideChatlistUpdatesHandler(
    IChatlistUpdateResolver updateResolver,
    IChatlistHiddenUpdateStore hiddenUpdateStore)
    : RpcResultObjectHandler<RequestHideChatlistUpdates, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestHideChatlistUpdates obj)
    {
        var info = await updateResolver.ResolveAsync(input.UserId, obj.Chatlist);

        await hiddenUpdateStore.HideAsync(input.UserId, info.Folder.Filter.Id,
            [.. info.MissingPeers.Select(p => p.PeerId)]);

        return new TBoolTrue();
    }
}
