namespace MyTelegram.Messenger.Services.Folders;

/// <param name="Title">The English name the live service serves; clients show it verbatim.</param>
/// <param name="Description">The line under the name in the "Recommended folders" list.</param>
/// <param name="Contacts">Include private chats with contacts.</param>
/// <param name="NonContacts">Include private chats with non-contacts.</param>
/// <param name="Groups">Include groups.</param>
/// <param name="Broadcasts">Include channels.</param>
/// <param name="Bots">Include bots.</param>
/// <param name="ExcludeMuted">Drop muted chats.</param>
/// <param name="ExcludeRead">Drop chats with nothing unread.</param>
public record SuggestedDialogFilter(
    string Title,
    string Description,
    bool Contacts = false,
    bool NonContacts = false,
    bool Groups = false,
    bool Broadcasts = false,
    bool Bots = false,
    bool ExcludeMuted = false,
    bool ExcludeRead = false
);

/// <summary>
/// The folder combinations offered before a user has built any of their own.
///
/// <para>Two of the entries are copied verbatim from the live service, which answered exactly
/// <c>Unread</c> and <c>Personal</c> for a probing account (2026-08-27). It suppresses a suggestion whose flag
/// set a folder of that account already matches exactly — that account owns groups-only, channels-only and
/// bots-only folders and got neither <c>Groups</c>, <c>Channels</c> nor <c>Bots</c> offered — so the
/// suppression rule below is measured, while the four remaining descriptions are extrapolated from the two
/// measured ones and are the only part of this list that is not verbatim. They can be measured on a fresh
/// account that owns no folders.</para>
/// See https://corefork.telegram.org/api/folders
/// </summary>
public interface ISuggestedDialogFilterCatalog
{
    /// <summary>Every suggestion, in the order they are served.</summary>
    IReadOnlyList<SuggestedDialogFilter> All { get; }

    /// <summary>
    /// The suggestions left after dropping the ones the user has already built, compared by flag set alone —
    /// the peer lists of a folder play no part, as measured against the live service.
    /// </summary>
    IReadOnlyList<SuggestedDialogFilter> GetAvailable(IEnumerable<DialogFilter> existingFilters);
}

/// <inheritdoc />
public class SuggestedDialogFilterCatalog : ISuggestedDialogFilterCatalog, ISingletonDependency
{
    /// <summary>Measured verbatim; the four type-based descriptions follow the pattern of the first two.</summary>
    public IReadOnlyList<SuggestedDialogFilter> All { get; } =
    [
        new("Unread", "New messages from all chats.", Contacts: true, NonContacts: true, Groups: true,
            Broadcasts: true, Bots: true, ExcludeRead: true),
        new("Personal", "Only messages from personal chats.", Contacts: true, NonContacts: true),
        new("Unmuted", "Only messages from unmuted chats.", Contacts: true, NonContacts: true, Groups: true,
            Broadcasts: true, Bots: true, ExcludeMuted: true),
        new("Groups", "Only messages from groups.", Groups: true),
        new("Channels", "Only messages from channels.", Broadcasts: true),
        new("Bots", "Only messages from bots.", Bots: true)
    ];

    public IReadOnlyList<SuggestedDialogFilter> GetAvailable(IEnumerable<DialogFilter> existingFilters)
    {
        var existingFlagSets = existingFilters.Select(FlagsOf).ToHashSet();

        return [.. All.Where(p => !existingFlagSets.Contains(FlagsOf(p)))];
    }

    private static (bool, bool, bool, bool, bool, bool, bool) FlagsOf(SuggestedDialogFilter filter)
    {
        return (filter.Contacts, filter.NonContacts, filter.Groups, filter.Broadcasts, filter.Bots,
            filter.ExcludeMuted, filter.ExcludeRead);
    }

    private static (bool, bool, bool, bool, bool, bool, bool) FlagsOf(DialogFilter filter)
    {
        return (filter.Contacts, filter.NonContacts, filter.Groups, filter.Broadcasts, filter.Bots,
            filter.ExcludeMuted, filter.ExcludeRead);
    }
}
