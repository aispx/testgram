namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Reorder <a href="https://corefork.telegram.org/api/folders">folders</a>
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.updateDialogFiltersOrder"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The order has to be stored, because clients adopt the server's vector verbatim: Android
/// numbers <c>filter.order</c> by the position a folder had in the <c>messages.getDialogFilters</c>
/// answer (<c>MessagesStorage.checkLoadedRemoteFilters</c>), so a reorder that is not persisted is
/// undone on the next start.</para>
/// </remarks>
internal sealed class UpdateDialogFiltersOrderHandler(ICommandBus commandBus, IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUpdateDialogFiltersOrder, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateDialogFiltersOrder obj)
    {
        var filters = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(input.UserId));
        var knownFilterIds = filters.Select(p => p.Filter.Id).ToHashSet();

        var order = new List<int>();
        foreach (var filterId in obj.Order ?? [])
        {
            // 0 is dialogFilterDefault ("All chats"): it has no folder of its own but it does own a slot
            // in the order, and clients send it — FilterTabsView adds 0 for the default filter.
            if (filterId != 0 && !knownFilterIds.Contains(filterId))
            {
                RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            }

            if (!order.Contains(filterId))
            {
                order.Add(filterId);
            }
        }

        var command = new UpdateDialogFiltersOrderCommand(
            DialogFilterSettingsId.Create(input.UserId),
            input.ToRequestInfo(),
            input.UserId,
            order);

        await commandBus.PublishAsync(command, CancellationToken.None);

        return null!;
    }
}
