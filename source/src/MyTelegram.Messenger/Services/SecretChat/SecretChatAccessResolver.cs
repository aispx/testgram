namespace MyTelegram.Messenger.Services.SecretChat;

public class SecretChatAccessResolver(IQueryProcessor queryProcessor)
    : ISecretChatAccessResolver, ITransientDependency
{
    public async Task EnsureUserCallerAsync(IRequestInput input)
    {
        if (input.UserId == 0)
        {
            RpcErrors.RpcErrors403.UserInvalid.ThrowRpcError();
        }

        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null)
        {
            RpcErrors.RpcErrors403.UserInvalid.ThrowRpcError();
        }

        if (userReadModel!.Bot)
        {
            RpcErrors.RpcErrors400.BotMethodInvalid.ThrowRpcError();
        }
    }

    public async Task<SecretChatAccess> ResolveAsync(IRequestInput input, IInputEncryptedChat peer)
    {
        await EnsureUserCallerAsync(input);

        if (peer is not TInputEncryptedChat inputEncryptedChat)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();

            return null!;
        }

        var chat = await GetChatAsync(inputEncryptedChat.ChatId);

        if (chat.AccessHash != inputEncryptedChat.AccessHash)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        return BuildAccess(chat, input.UserId);
    }

    public async Task<SecretChatAccess> ResolveByChatIdAsync(IRequestInput input, long chatId)
    {
        await EnsureUserCallerAsync(input);

        var chat = await GetChatAsync(chatId);

        return BuildAccess(chat, input.UserId);
    }

    public void RequireActive(SecretChatAccess access, bool forSend)
    {
        switch (access.Chat.ChatState)
        {
            case ChatState.Active:
                return;
            case ChatState.Discarded when forSend:
                RpcErrors.RpcErrors400.EncryptionDeclined.ThrowRpcError();
                break;
            default:
                // Waiting (not yet accepted, no bound recipient device) or Discarded for read/typing.
                RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
                break;
        }
    }

    private async Task<IEncryptedChatReadModel> GetChatAsync(long chatId)
    {
        var chat = await queryProcessor.ProcessAsync(new GetEncryptedChatByIdQuery(chatId));
        if (chat == null)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        return chat!;
    }

    private static SecretChatAccess BuildAccess(IEncryptedChatReadModel chat, long callerUserId)
    {
        if (callerUserId != chat.AdminId && callerUserId != chat.ParticipantId)
        {
            RpcErrors.RpcErrors400.EncryptionIdInvalid.ThrowRpcError();
        }

        return new SecretChatAccess(chat, callerUserId == chat.AdminId);
    }
}
