namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/folders">suggested folders</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getSuggestedDialogFilters"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>These are the entries of the "Recommended folders" block of the folder setup screen. An empty answer,
/// which is what this used to be, removes the block entirely. A suggestion the user already built is dropped,
/// compared by flag set alone — the behaviour of the live service, measured on an account whose groups-only,
/// channels-only and bots-only folders suppressed exactly those three suggestions.</para>
/// </remarks>
internal sealed class GetSuggestedDialogFiltersHandler(
    IQueryProcessor queryProcessor,
    ISuggestedDialogFilterCatalog catalog)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSuggestedDialogFilters,
        TVector<MyTelegram.Schema.IDialogFilterSuggested>>
{
    protected override async Task<TVector<IDialogFilterSuggested>> HandleCoreAsync(IRequestInput input,
        RequestGetSuggestedDialogFilters obj)
    {
        var filters = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(input.UserId));
        var available = catalog.GetAvailable(filters.Select(p => p.Filter));

        var suggestions = new TVector<IDialogFilterSuggested>();
        foreach (var suggestion in available)
        {
            suggestions.Add(new TDialogFilterSuggested
            {
                Description = suggestion.Description,
                Filter = new TDialogFilter
                {
                    // The live service sends placeholder ids here; the client picks a free id of its own when
                    // the user accepts a suggestion.
                    Id = 0,
                    Title = new TTextWithEntities
                    {
                        Text = suggestion.Title,
                        Entities = new TVector<IMessageEntity>()
                    },
                    Contacts = suggestion.Contacts,
                    NonContacts = suggestion.NonContacts,
                    Groups = suggestion.Groups,
                    Broadcasts = suggestion.Broadcasts,
                    Bots = suggestion.Bots,
                    ExcludeMuted = suggestion.ExcludeMuted,
                    ExcludeRead = suggestion.ExcludeRead,
                    PinnedPeers = new TVector<IInputPeer>(),
                    IncludePeers = new TVector<IInputPeer>(),
                    ExcludePeers = new TVector<IInputPeer>()
                }
            });
        }

        return suggestions;
    }
}
