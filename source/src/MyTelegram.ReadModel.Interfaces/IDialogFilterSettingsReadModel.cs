namespace MyTelegram.ReadModel.Interfaces;

/// <summary>
/// The per-user settings of the <a href="https://corefork.telegram.org/api/folders">folders</a> surface.
/// </summary>
public interface IDialogFilterSettingsReadModel : IReadModel
{
    long OwnerUserId { get; }

    /// <summary>
    /// The order clients see their folders in, as sent by <c>messages.updateDialogFiltersOrder</c>.
    /// Contains <c>0</c> for <c>dialogFilterDefault</c>.
    /// </summary>
    IReadOnlyList<int> Order { get; }

    /// <summary><c>messages.dialogFilters.tags_enabled</c>.</summary>
    bool TagsEnabled { get; }

    /// <summary>Whether the chat archive is pinned to the top of the main dialog list.</summary>
    bool ArchivePinned { get; }
}
