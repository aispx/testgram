using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Delete a previously created <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// 400 INVITE_SLUG_INVALID The specified invitation slug is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.deleteExportedInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The link is revoked rather than removed, so a client that still holds the slug is told the link
/// expired instead of being handed a folder again.</para>
/// </remarks>
internal sealed class DeleteExportedInviteHandler(IChatlistInviteStore chatlistInviteStore)
    : RpcResultObjectHandler<RequestDeleteExportedInvite, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDeleteExportedInvite obj)
    {
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var revoked = await chatlistInviteStore.RevokeAsync(obj.Slug, input.UserId, chatlistFilter.FilterId);
        if (!revoked)
        {
            RpcErrors.RpcErrors400.InviteSlugInvalid.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
