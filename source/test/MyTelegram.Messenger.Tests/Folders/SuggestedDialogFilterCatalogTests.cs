using MyTelegram.Messenger.Services.Folders;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Folders;

/// <summary>
/// Feature: <c>messages.getSuggestedDialogFilters</c>, the "Recommended folders" block.
///
/// <para>The method used to answer an empty vector, which removes the block from the folder setup screen. Two
/// of the six entries are copied verbatim from the live service, and so is the suppression rule: an account
/// owning a groups-only, a channels-only and a bots-only folder was offered neither <c>Groups</c> nor
/// <c>Channels</c> nor <c>Bots</c>, but still got <c>Personal</c> although it owned a contacts-only and a
/// non-contacts-only folder — so the comparison is on the whole flag set, not on any overlap.</para>
/// </summary>
public class SuggestedDialogFilterCatalogTests
{
    private readonly SuggestedDialogFilterCatalog _catalog = new();

    [Fact]
    public void The_two_measured_suggestions_are_served_verbatim()
    {
        var unread = _catalog.All.First();
        unread.Title.ShouldBe("Unread");
        unread.Description.ShouldBe("New messages from all chats.");
        (unread.Contacts, unread.NonContacts, unread.Groups, unread.Broadcasts, unread.Bots, unread.ExcludeRead)
            .ShouldBe((true, true, true, true, true, true));
        unread.ExcludeMuted.ShouldBeFalse();

        var personal = _catalog.All[1];
        personal.Title.ShouldBe("Personal");
        personal.Description.ShouldBe("Only messages from personal chats.");
        (personal.Contacts, personal.NonContacts).ShouldBe((true, true));
        (personal.Groups, personal.Broadcasts, personal.Bots).ShouldBe((false, false, false));
    }

    [Fact]
    public void A_folder_with_the_same_flags_removes_its_suggestion()
    {
        var available = _catalog.GetAvailable([Filter(groups: true)]);

        available.Select(p => p.Title).ShouldNotContain("Groups");
        available.Select(p => p.Title).ShouldContain("Channels");
        available.Count.ShouldBe(_catalog.All.Count - 1);
    }

    [Fact]
    public void Overlapping_but_different_flags_keep_the_suggestion()
    {
        // Contacts-only and non-contacts-only folders do not add up to "Personal" for the live service.
        var available = _catalog.GetAvailable([Filter(contacts: true), Filter(nonContacts: true)]);

        available.Select(p => p.Title).ShouldContain("Personal");
    }

    [Fact]
    public void Peer_lists_play_no_part_in_the_comparison()
    {
        var withPeers = Filter(groups: true) with
        {
            IncludePeers = [new InputPeer(new Peer(PeerType.Channel, 1), 0)]
        };

        _catalog.GetAvailable([withPeers]).Select(p => p.Title).ShouldNotContain("Groups");
    }

    [Fact]
    public void An_account_with_no_folders_is_offered_everything()
    {
        _catalog.GetAvailable([]).Count.ShouldBe(6);
    }

    private static DialogFilter Filter(bool contacts = false, bool nonContacts = false, bool groups = false)
    {
        return new DialogFilter(2, contacts, nonContacts, groups, false, false, false, false, false, false,
            new TTextWithEntities { Text = "Folder", Entities = [] }, null, null, [], [], [], false);
    }
}
