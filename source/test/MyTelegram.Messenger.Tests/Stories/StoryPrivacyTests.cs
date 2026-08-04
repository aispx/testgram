using MyTelegram.Messenger.Services.Stories;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — privacy evaluation.
///
/// <para>
/// <see cref="StoryHelper.CanViewStory"/> is the single gate deciding whether a viewer may see a story,
/// and it is applied by every read path (getAllStories, getPeerStories, getStoriesByID, …). These tests
/// pin down the rule matrix, in particular the interactions that a naive "return on first matching rule"
/// implementation gets wrong: disallow rules must win over allow rules regardless of order, and
/// close-friends must resolve against the <em>owner's</em> list rather than the viewer's contacts.
/// </para>
/// </summary>
public class StoryPrivacyTests
{
    private const long OwnerId = 100;
    private const long ViewerId = 200;

    [Fact]
    public void Owner_always_sees_own_story_even_when_disallowed_by_rules()
    {
        var story = Story(Rule(StoryPrivacyRuleType.DisallowAll));

        StoryHelper.CanViewStory(story, OwnerId, Context()).ShouldBeTrue();
    }

    [Fact]
    public void Story_without_rules_is_visible()
    {
        var story = Story();

        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeTrue();
    }

    [Fact]
    public void AllowAll_is_visible_to_a_stranger()
    {
        var story = Story(Rule(StoryPrivacyRuleType.AllowAll));

        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeTrue();
    }

    [Fact]
    public void DisallowAll_hides_the_story_from_a_contact()
    {
        var story = Story(Rule(StoryPrivacyRuleType.DisallowAll));

        StoryHelper.CanViewStory(story, ViewerId, Context(isContact: true)).ShouldBeFalse();
    }

    [Fact]
    public void AllowContacts_admits_a_contact_and_rejects_a_stranger()
    {
        var story = Story(Rule(StoryPrivacyRuleType.AllowContacts));

        StoryHelper.CanViewStory(story, ViewerId, Context(isContact: true)).ShouldBeTrue();
        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeFalse();
    }

    [Fact]
    public void AllowCloseFriends_admits_only_a_close_friend()
    {
        var story = Story(Rule(StoryPrivacyRuleType.AllowCloseFriends));

        StoryHelper.CanViewStory(story, ViewerId, Context(isCloseFriend: true)).ShouldBeTrue();
        // A plain contact who is not on the close-friends list must not see it.
        StoryHelper.CanViewStory(story, ViewerId, Context(isContact: true)).ShouldBeFalse();
        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeFalse();
    }

    [Fact]
    public void AllowUsers_matches_every_listed_user_not_just_the_first()
    {
        var story = Story(new StoryPrivacyRule
        {
            Type = StoryPrivacyRuleType.AllowUsers,
            UserIds = [300, ViewerId, 400]
        });

        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeTrue();
        StoryHelper.CanViewStory(story, 999, Context()).ShouldBeFalse();
    }

    [Fact]
    public void DisallowUsers_beats_AllowContacts_regardless_of_rule_order()
    {
        var disallowFirst = Story(
            new StoryPrivacyRule { Type = StoryPrivacyRuleType.DisallowUsers, UserIds = [ViewerId] },
            Rule(StoryPrivacyRuleType.AllowContacts));

        var allowFirst = Story(
            Rule(StoryPrivacyRuleType.AllowContacts),
            new StoryPrivacyRule { Type = StoryPrivacyRuleType.DisallowUsers, UserIds = [ViewerId] });

        StoryHelper.CanViewStory(disallowFirst, ViewerId, Context(isContact: true)).ShouldBeFalse();
        StoryHelper.CanViewStory(allowFirst, ViewerId, Context(isContact: true)).ShouldBeFalse();
    }

    [Fact]
    public void DisallowContacts_only_excludes_contacts()
    {
        var story = Story(Rule(StoryPrivacyRuleType.DisallowContacts));

        StoryHelper.CanViewStory(story, ViewerId, Context(isContact: true)).ShouldBeFalse();
        // Only a disallow rule is present, so anyone not excluded may still watch.
        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeTrue();
    }

    [Fact]
    public void Allow_rules_are_additive()
    {
        var story = Story(
            Rule(StoryPrivacyRuleType.AllowCloseFriends),
            new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowUsers, UserIds = [ViewerId] });

        // Matching either allow rule is enough: the viewer is on the allow-users list...
        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeTrue();
        // ...and a close friend passes through the other rule even without being listed.
        StoryHelper.CanViewStory(story, 999, Context(isCloseFriend: true)).ShouldBeTrue();
        // Someone matching neither rule is refused.
        StoryHelper.CanViewStory(story, 999, Context(isContact: true)).ShouldBeFalse();
    }

    [Fact]
    public void AllowPremium_checks_the_viewers_premium_status()
    {
        var story = Story(Rule(StoryPrivacyRuleType.AllowPremium));

        StoryHelper.CanViewStory(story, ViewerId, Context(isPremium: true)).ShouldBeTrue();
        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeFalse();
    }

    [Fact]
    public void Close_friend_status_is_scoped_to_the_story_owner()
    {
        var story = Story(Rule(StoryPrivacyRuleType.AllowCloseFriends));

        // The viewer is a close friend of some other user, not of this story's owner.
        var context = new StoryViewerContext
        {
            UserId = ViewerId,
            OwnersWhoHaveViewerAsCloseFriend = [999]
        };

        StoryHelper.CanViewStory(story, ViewerId, context).ShouldBeFalse();
    }

    [Fact]
    public void Channel_stories_bypass_user_privacy_rules()
    {
        var story = Story(Rule(StoryPrivacyRuleType.DisallowAll));
        story.OwnerPeerType = StoryHelper.PeerTypeChannel;

        // Channel visibility is decided by channel membership before this point.
        StoryHelper.CanViewStory(story, ViewerId, Context()).ShouldBeTrue();
    }

    private static StoryPrivacyRule Rule(int type) => new() { Type = type };

    private static StoryDocument Story(params StoryPrivacyRule[] rules) => new()
    {
        OwnerPeerId = OwnerId,
        OwnerPeerType = StoryHelper.PeerTypeUser,
        StoryId = 1,
        PrivacyRules = rules.ToList()
    };

    private static StoryViewerContext Context(
        bool isContact = false,
        bool isCloseFriend = false,
        bool isPremium = false)
    {
        return new StoryViewerContext
        {
            UserId = ViewerId,
            IsPremium = isPremium,
            OwnersWhoHaveViewerAsContact = isContact ? [OwnerId] : [],
            OwnersWhoHaveViewerAsCloseFriend = isCloseFriend ? [OwnerId] : []
        };
    }
}
