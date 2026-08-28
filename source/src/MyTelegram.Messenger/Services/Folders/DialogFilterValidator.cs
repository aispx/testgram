namespace MyTelegram.Messenger.Services.Folders;

/// <param name="FilterId">The folder id the request names.</param>
/// <param name="Filter">The folder as it arrived, either <c>dialogFilter</c> or <c>dialogFilterChatlist</c>.</param>
/// <param name="IsNewFilter">Whether the user has no folder with this id yet.</param>
/// <param name="ExistingIsChatlist">Whether the stored folder is a shareable one.</param>
/// <param name="ExistingFilterCount">How many folders the user has, not counting <c>dialogFilterDefault</c>.</param>
/// <param name="FilterLimit"><c>dialog_filters_limit</c>.</param>
/// <param name="ChatsPerFilterLimit"><c>dialog_filters_chats_limit</c>.</param>
/// <param name="PinnedPerFilterLimit"><c>dialogs_folder_pinned_limit</c>.</param>
public record DialogFilterValidationContext(
    int FilterId,
    IDialogFilter Filter,
    bool IsNewFilter,
    bool ExistingIsChatlist,
    int ExistingFilterCount,
    int FilterLimit,
    int ChatsPerFilterLimit,
    int PinnedPerFilterLimit
);

/// <summary>
/// What <c>messages.updateDialogFilter</c> refuses. Nothing was checked before, so a folder with no title,
/// no chats, or one holding the id of <c>dialogFilterDefault</c> was stored and then served to every client.
/// </summary>
public interface IDialogFilterValidator
{
    void Validate(DialogFilterValidationContext context);
}

/// <inheritdoc />
public class DialogFilterValidator : IDialogFilterValidator, ITransientDependency
{
    /// <summary>
    /// 0 is <c>dialogFilterDefault</c> and 1 is reserved; clients allocate from 2 upwards
    /// (<c>FilterCreateActivity</c>: <c>filter.id = 2; while (dialogFiltersById.get(filter.id) != null)</c>).
    /// </summary>
    public const int MinFilterId = 2;

    /// <summary>
    /// The name length every official client enforces (<c>FilterCreateActivity.MAX_NAME_LENGTH</c>), counted
    /// in UTF-16 units exactly as they count it.
    /// </summary>
    public const int MaxTitleLength = 12;

    public void Validate(DialogFilterValidationContext context)
    {
        if (context.FilterId < MinFilterId)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        // A folder cannot change kind: tdlib and Android send back the constructor they received, and an
        // imported folder that turned into a plain one would lose the invite it belongs to.
        var isChatlistRequest = context.Filter is TDialogFilterChatlist;
        if (!context.IsNewFilter && context.ExistingIsChatlist != isChatlistRequest)
        {
            if (context.ExistingIsChatlist && context.Filter is TDialogFilter { ExcludePeers.Count: > 0 })
            {
                RpcErrors.RpcErrors400.ChatlistExcludeInvalid.ThrowRpcError();
            }

            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        if (context.IsNewFilter && context.ExistingFilterCount >= context.FilterLimit)
        {
            // Not in RpcErrors.g.cs, but it is the string Android maps to the folders limit sheet
            // (FilterCreateActivity.processErrors).
            throw new RpcException(new RpcError(400, "DIALOG_FILTERS_TOO_MUCH"));
        }

        switch (context.Filter)
        {
            case TDialogFilter dialogFilter:
                ValidateTitle(dialogFilter.Title);
                ValidatePeerCounts(context, dialogFilter.PinnedPeers, dialogFilter.IncludePeers);

                var hasTypeFlag = dialogFilter.Contacts
                                  || dialogFilter.NonContacts
                                  || dialogFilter.Groups
                                  || dialogFilter.Broadcasts
                                  || dialogFilter.Bots;
                if (!hasTypeFlag && dialogFilter.IncludePeers.Count == 0 && dialogFilter.PinnedPeers.Count == 0)
                {
                    RpcErrors.RpcErrors400.FilterIncludeEmpty.ThrowRpcError();
                }

                break;

            case TDialogFilterChatlist dialogFilterChatlist:
                ValidateTitle(dialogFilterChatlist.Title);
                ValidatePeerCounts(context, dialogFilterChatlist.PinnedPeers, dialogFilterChatlist.IncludePeers);

                if (dialogFilterChatlist.IncludePeers.Count == 0 && dialogFilterChatlist.PinnedPeers.Count == 0)
                {
                    RpcErrors.RpcErrors400.FilterIncludeEmpty.ThrowRpcError();
                }

                break;

            default:
                // dialogFilterDefault cannot be created or edited.
                RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
                break;
        }
    }

    private static void ValidateTitle(ITextWithEntities? title)
    {
        if (string.IsNullOrWhiteSpace(title?.Text))
        {
            RpcErrors.RpcErrors400.FilterTitleEmpty.ThrowRpcError();
        }

        if (title!.Text.Length > MaxTitleLength)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }
    }

    private static void ValidatePeerCounts(DialogFilterValidationContext context,
        TVector<IInputPeer> pinnedPeers,
        TVector<IInputPeer> includePeers)
    {
        if (pinnedPeers.Count > context.PinnedPerFilterLimit)
        {
            RpcErrors.RpcErrors400.PinnedDialogsTooMuch.ThrowRpcError();
        }

        if (pinnedPeers.Count + includePeers.Count > context.ChatsPerFilterLimit)
        {
            // Android maps this one to the "chats in folder" limit sheet.
            throw new RpcException(new RpcError(400, "FILTER_INCLUDE_TOO_MUCH"));
        }
    }
}
