using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — privacy rule parsing and the flags derived from it.
///
/// <para>
/// The rules a client sends with sendStory/editStory are stored and later replayed both to the owner
/// (as <c>storyItem.privacy</c>) and, in summarised form, to every viewer (the public/contacts/
/// close_friends/selected_contacts flags). An earlier implementation kept only the first user of an
/// allow-users rule, silently dropping the rest of the audience.
/// </para>
/// </summary>
public class StoryPrivacyRuleParsingTests
{
    [Fact]
    public void Keeps_every_user_of_an_allow_users_rule()
    {
        var rules = StoryHelper.ParsePrivacyRules(
        [
            new TInputPrivacyValueAllowUsers
            {
                Users =
                [
                    new TInputUser { UserId = 1, AccessHash = 0 },
                    new TInputUser { UserId = 2, AccessHash = 0 },
                    new TInputUser { UserId = 3, AccessHash = 0 }
                ]
            }
        ]);

        rules.Count.ShouldBe(1);
        rules[0].Type.ShouldBe(StoryPrivacyRuleType.AllowUsers);
        rules[0].UserIds.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Keeps_every_user_of_a_disallow_users_rule()
    {
        var rules = StoryHelper.ParsePrivacyRules(
        [
            new TInputPrivacyValueDisallowUsers
            {
                Users =
                [
                    new TInputUser { UserId = 7, AccessHash = 0 },
                    new TInputUser { UserId = 8, AccessHash = 0 }
                ]
            }
        ]);

        rules[0].Type.ShouldBe(StoryPrivacyRuleType.DisallowUsers);
        rules[0].UserIds.ShouldBe([7, 8]);
    }

    [Fact]
    public void Parses_chat_participant_rules()
    {
        var rules = StoryHelper.ParsePrivacyRules(
        [
            new TInputPrivacyValueAllowChatParticipants { Chats = [10, 11] },
            new TInputPrivacyValueDisallowChatParticipants { Chats = [12] }
        ]);

        rules[0].Type.ShouldBe(StoryPrivacyRuleType.AllowChatParticipants);
        rules[0].ChatIds.ShouldBe([10, 11]);
        rules[1].Type.ShouldBe(StoryPrivacyRuleType.DisallowChatParticipants);
        rules[1].ChatIds.ShouldBe([12]);
    }

    [Theory]
    [InlineData(typeof(TInputPrivacyValueAllowAll), StoryPrivacyRuleType.AllowAll)]
    [InlineData(typeof(TInputPrivacyValueAllowContacts), StoryPrivacyRuleType.AllowContacts)]
    [InlineData(typeof(TInputPrivacyValueDisallowAll), StoryPrivacyRuleType.DisallowAll)]
    [InlineData(typeof(TInputPrivacyValueDisallowContacts), StoryPrivacyRuleType.DisallowContacts)]
    [InlineData(typeof(TInputPrivacyValueAllowCloseFriends), StoryPrivacyRuleType.AllowCloseFriends)]
    [InlineData(typeof(TInputPrivacyValueAllowPremium), StoryPrivacyRuleType.AllowPremium)]
    public void Maps_the_simple_rules_to_their_stored_discriminators(Type inputType, int expectedType)
    {
        var input = (IInputPrivacyRule)Activator.CreateInstance(inputType)!;

        StoryHelper.ParsePrivacyRules([input])[0].Type.ShouldBe(expectedType);
    }

    [Fact]
    public void Converting_back_preserves_the_full_user_list()
    {
        var stored = new List<StoryPrivacyRule>
        {
            new() { Type = StoryPrivacyRuleType.AllowUsers, UserIds = [1, 2, 3] }
        };

        var converted = StoryHelper.ConvertPrivacyRules(stored);

        converted.Count.ShouldBe(1);
        converted[0].ShouldBeOfType<TPrivacyValueAllowUsers>().Users.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Empty_user_lists_are_dropped_rather_than_emitted_as_empty_rules()
    {
        var converted = StoryHelper.ConvertPrivacyRules(
        [
            new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowUsers, UserIds = [] }
        ]);

        converted.ShouldBeEmpty();
    }

    [Fact]
    public void Privacy_rules_are_only_exposed_to_the_owner()
    {
        var story = StoryWithRules(StoryPrivacyRuleType.AllowContacts);

        var forOwner = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper, story, 100, includePrivacy: true);
        var forViewer = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper, story, 200);

        forOwner.Privacy.ShouldNotBeNull();
        // A viewer must not learn the owner's audience configuration.
        forViewer.Privacy.ShouldBeNull();
    }

    [Fact]
    public void Audience_flags_are_derived_for_every_viewer()
    {
        var contactsOnly = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper,
            StoryWithRules(StoryPrivacyRuleType.AllowContacts), 200);
        contactsOnly.Contacts.ShouldBeTrue();
        contactsOnly.Public.ShouldBeFalse();

        var closeFriends = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper,
            StoryWithRules(StoryPrivacyRuleType.AllowCloseFriends), 200);
        closeFriends.CloseFriends.ShouldBeTrue();

        var selected = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper,
            StoryWithRules(StoryPrivacyRuleType.AllowUsers), 200);
        selected.SelectedContacts.ShouldBeTrue();

        var everyone = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper,
            StoryWithRules(StoryPrivacyRuleType.AllowAll), 200);
        everyone.Public.ShouldBeTrue();
    }

    [Fact]
    public void Out_and_min_reflect_who_is_reading()
    {
        var story = StoryWithRules(StoryPrivacyRuleType.AllowAll);

        var forOwner = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper, story, 100);
        forOwner.Out.ShouldBeTrue();
        forOwner.Min.ShouldBeFalse();

        var forViewer = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper, story, 200);
        forViewer.Out.ShouldBeFalse();
        forViewer.Min.ShouldBeTrue();
    }

    [Fact]
    public void Album_membership_and_the_viewers_own_reaction_are_reported()
    {
        var story = StoryWithRules(StoryPrivacyRuleType.AllowAll);
        story.AlbumIds = [4, 9];

        var item = (TStoryItem)StoryHelper.ConvertToStoryItem(TestFileReferences.Helper,
            story, 200, new TReactionEmoji { Emoticon = "🔥" });

        item.Albums.ShouldBe([4, 9]);
        item.SentReaction.ShouldBeOfType<TReactionEmoji>().Emoticon.ShouldBe("🔥");
    }

    [Fact]
    public void An_expired_story_is_not_the_deleted_placeholder()
    {
        // Expiry moves a story to the archive; only real deletion produces storyItemDeleted.
        // See https://corefork.telegram.org/api/stories — pinning exists so an expired story stays
        // on the profile, and stories.getPinnedStories / getStoriesArchive serve exactly those.
        // This previously asserted the opposite, which is what emptied both listings.
        var story = StoryWithRules(StoryPrivacyRuleType.AllowAll);
        story.ExpireDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;

        StoryHelper.ConvertToStoryItem(TestFileReferences.Helper, story, 100).ShouldBeOfType<TStoryItem>();
    }

    [Fact]
    public void A_deleted_story_converts_to_the_deleted_placeholder()
    {
        var story = StoryWithRules(StoryPrivacyRuleType.AllowAll);
        story.Deleted = true;

        StoryHelper.ConvertToStoryItem(TestFileReferences.Helper, story, 100).ShouldBeOfType<TStoryItemDeleted>();
    }

    private static StoryDocument StoryWithRules(params int[] ruleTypes) => new()
    {
        OwnerPeerId = 100,
        OwnerPeerType = StoryHelper.PeerTypeUser,
        StoryId = 1,
        Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10,
        ExpireDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400,
        MediaType = 1,
        MediaFileId = 12345,
        MediaAccessHash = 1,
        MediaDcId = 2,
        PrivacyRules = ruleTypes
            .Select(t => new StoryPrivacyRule
            {
                Type = t,
                UserIds = t == StoryPrivacyRuleType.AllowUsers ? [200] : []
            })
            .ToList()
    };
}
