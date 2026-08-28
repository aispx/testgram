namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/folders">folders</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDialogFilters"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The order of the vector is the contract: clients adopt it verbatim and number their own tabs by
/// it (Android's <c>MessagesStorage.checkLoadedRemoteFilters</c> assigns <c>filter.order</c> from the
/// position), so the stored order has to be replayed here, including the slot of
/// <c>dialogFilterDefault</c> — clients send <c>0</c> for it in
/// <c>messages.updateDialogFiltersOrder</c>.</para>
/// </remarks>
internal sealed class GetDialogFiltersHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper2,
    IChatlistInviteStore chatlistInviteStore,
    ILayeredService<IDialogFilterConverter> dialogFilterLayeredService)
    : RpcResultObjectHandler<RequestGetDialogFilters, IDialogFilters>
{
    private const int DefaultFilterId = 0;

    protected override async Task<IDialogFilters> HandleCoreAsync(IRequestInput input, RequestGetDialogFilters obj)
    {
        if (input.UserId == 0)
        {
            return new TDialogFilters
            {
                Filters = [new TDialogFilterDefault()],
                TagsEnabled = false
            };
        }

        var filterReadModels = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(input.UserId));
        var settings = await queryProcessor.ProcessAsync(new GetDialogFilterSettingsQuery(input.UserId));
        var filterIdsWithInvites = await chatlistInviteStore.GetFilterIdsWithInvitesAsync(input.UserId);
        var converter = dialogFilterLayeredService.GetConverter(input.Layer);

        var filters = new TVector<IDialogFilter>();
        foreach (var filterId in OrderFilterIds(filterReadModels, settings?.Order))
        {
            // 0 is the position of "All chats" among the tabs.
            if (filterId == DefaultFilterId)
            {
                filters.Add(new TDialogFilterDefault());
                continue;
            }

            var readModel = filterReadModels.First(p => p.Filter.Id == filterId);
            var filter = converter.ToDialogFilter(readModel.Filter, filterIdsWithInvites.Contains(filterId));
            filters.Add(filter);

            switch (filter)
            {
                case TDialogFilter dialogFilter:
                    UpdateAccessHash(input, dialogFilter.ExcludePeers);
                    UpdateAccessHash(input, dialogFilter.IncludePeers);
                    UpdateAccessHash(input, dialogFilter.PinnedPeers);
                    break;
                case TDialogFilterChatlist dialogFilterChatlist:
                    UpdateAccessHash(input, dialogFilterChatlist.PinnedPeers);
                    UpdateAccessHash(input, dialogFilterChatlist.IncludePeers);
                    break;
            }
        }

        return new TDialogFilters
        {
            Filters = filters,
            // Folder tags are a per-user setting behind a subscription; the live service answers false for
            // an account without one, and Android mirrors whatever arrives here into its own state.
            TagsEnabled = settings?.TagsEnabled == true
        };
    }

    /// <summary>
    /// The stored order first, then any folder the order does not mention (one created after the last
    /// reorder), lowest id first. <c>dialogFilterDefault</c> keeps its stored slot and goes first when the
    /// order does not name it.
    /// </summary>
    private static List<int> OrderFilterIds(IReadOnlyCollection<IDialogFilterReadModel> filterReadModels,
        IReadOnlyList<int>? storedOrder)
    {
        var known = filterReadModels.Select(p => p.Filter.Id).Where(p => p != DefaultFilterId).ToHashSet();
        var ordered = new List<int>();

        foreach (var filterId in storedOrder ?? [])
        {
            if (filterId == DefaultFilterId)
            {
                if (!ordered.Contains(DefaultFilterId))
                {
                    ordered.Add(DefaultFilterId);
                }

                continue;
            }

            if (known.Remove(filterId))
            {
                ordered.Add(filterId);
            }
        }

        if (!ordered.Contains(DefaultFilterId))
        {
            ordered.Insert(0, DefaultFilterId);
        }

        ordered.AddRange(known.Order());

        return ordered;
    }

    private void UpdateAccessHash(IRequestInput requestInput, TVector<IInputPeer> peers)
    {
        foreach (var inputPeer in peers)
        {
            switch (inputPeer)
            {
                case TInputPeerChannel inputPeerChannel:
                    inputPeerChannel.AccessHash = accessHashHelper2.GenerateAccessHash(requestInput.UserId, requestInput.AccessHashKeyId, inputPeerChannel.ChannelId, AccessHashType.Channel);
                    break;
                case TInputPeerUser inputPeerUser:
                    inputPeerUser.AccessHash = accessHashHelper2.GenerateAccessHash(requestInput.UserId, requestInput.AccessHashKeyId, inputPeerUser.UserId, AccessHashType.User);
                    break;
            }
        }
    }
}
