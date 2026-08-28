namespace MyTelegram.Domain.Aggregates.Dialog;

/// <summary>
/// The per-user settings of the <a href="https://corefork.telegram.org/api/folders">folders</a> surface:
/// the order clients see their folders in, the folder tags toggle and the pinned state of the archive.
///
/// <para>Order is a per-user value spanning every folder, so it cannot live on
/// <see cref="DialogFilterAggregate"/>: it also has to hold the slot of <c>dialogFilterDefault</c>,
/// which has no aggregate of its own (clients send <c>0</c> for it in
/// <c>messages.updateDialogFiltersOrder</c>).</para>
/// </summary>
[EnableAutoGeneration]
public class DialogFilterSettingsAggregate : AggregateRoot<DialogFilterSettingsAggregate, DialogFilterSettingsId>
{
    private readonly DialogFilterSettingsState _state = new();

    public DialogFilterSettingsAggregate(DialogFilterSettingsId id) : base(id)
    {
        Register(_state);
    }

    public void UpdateDialogFiltersOrder(RequestInfo requestInfo, long ownerUserId, List<int> order)
    {
        Emit(new DialogFiltersOrderUpdatedEvent(requestInfo, ownerUserId, order));
    }

    /// <summary>
    /// <c>messages.toggleDialogFilterTags</c>. The event is emitted even when the value did not change,
    /// because the RPC answer is produced by the domain event handler; <c>changed</c> is what decides
    /// whether <c>updateDialogFilters</c> is pushed to the other sessions ("If the new value of the
    /// toggle is different, the method will emit an updateDialogFilters to all other currently-logged
    /// in sessions").
    /// </summary>
    public void ToggleDialogFilterTags(RequestInfo requestInfo, long ownerUserId, bool enabled)
    {
        var changed = _state.TagsEnabled != enabled;
        Emit(new DialogFilterTagsToggledEvent(requestInfo, ownerUserId, enabled, changed));
    }

    /// <summary>
    /// <c>messages.toggleDialogPin</c> with an <c>inputDialogPeerFolder</c>: the archive itself can be
    /// pinned to the top of the main dialog list, and that is the only case in which the server sends a
    /// <c>dialogFolder</c> (measured against the live service, which sends none for an unpinned
    /// archive).
    /// </summary>
    public void UpdateArchivePinned(RequestInfo requestInfo, long ownerUserId, bool pinned)
    {
        var changed = _state.ArchivePinned != pinned;
        Emit(new DialogArchivePinnedUpdatedEvent(requestInfo, ownerUserId, pinned, changed));
    }
}
