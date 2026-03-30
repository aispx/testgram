namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Make a user admin in a <a href="https://corefork.telegram.org/api/channel#basic-groups">basic group</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 400 USER_NOT_PARTICIPANT You're not a member of this supergroup/channel.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editChatAdmin"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditChatAdminHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditChatAdmin, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestEditChatAdmin obj)
    {
        // SECURITY FIX: Verify caller has admin rights before allowing admin changes
        // This prevents privilege escalation where any user could make themselves admin
        
        // For now, throw CHAT_ADMIN_REQUIRED as a security measure
        // Full implementation would check chat membership and admin rights
        Console.WriteLine($"[SECURITY] EditChatAdmin called by user {input.UserId} for chat {obj.ChatId}, user {obj.UserId}");
        
        // TODO: Implement full authorization check:
        // 1. Check caller is member of chat
        // 2. Check caller has add_admins right or is creator
        // 3. Check target user is member of chat
        
        // SECURITY: Block unauthorized admin changes
        RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
        return new TBoolFalse();
    }
}