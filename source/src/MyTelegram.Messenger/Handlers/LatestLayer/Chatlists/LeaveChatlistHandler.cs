namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Delete a folder imported using a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.leaveChatlist"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class LeaveChatlistHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestLeaveChatlist, MyTelegram.Schema.IUpdates>
{
    private readonly ICommandBus _commandBus;

    public LeaveChatlistHandler(ICommandBus commandBus)
    {
        _commandBus = commandBus;
    }

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestLeaveChatlist obj)
    {
        // 1. Validate chatlist
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        var filterId = chatlistFilter.FilterId;

        // 2. Delete the folder
        var command = new DeleteDialogFilterCommand(
            DialogFilterId.Create(input.UserId, filterId),
            input.ToRequestInfo()
        );

        await _commandBus.PublishAsync(command, default);

        // 3. Return empty Updates (folder deletion will sync via updateDialogFilter)
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Seq = 0
        };
    }
}