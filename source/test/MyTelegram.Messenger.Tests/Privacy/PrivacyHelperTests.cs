using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Privacy;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.Privacy;

/// <summary>
/// Rule-evaluation tests for <see cref="PrivacyHelper"/>.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
public class PrivacyHelperTests
{
    private const long ViewerId = 100;
    private const long OtherId = 200;

    // --- classic rules: guard the priority order the newer rules were slotted into ---

    [Fact]
    public void ShouldAllowWhenNoRulesConfigured()
    {
        Evaluate([], ContactType.None).ShouldBeTrue();
    }

    [Fact]
    public void ShouldPreferDisallowUsersOverAllowAll()
    {
        var rules = new[]
        {
            Rule(PrivacyValueType.AllowAll),
            Ids(PrivacyValueType.DisallowUsers, ViewerId)
        };

        Evaluate(rules, ContactType.None).ShouldBeFalse();
    }

    [Fact]
    public void ShouldPreferAllowUsersOverDisallowAll()
    {
        var rules = new[]
        {
            Rule(PrivacyValueType.DisallowAll),
            Ids(PrivacyValueType.AllowUsers, ViewerId)
        };

        Evaluate(rules, ContactType.None).ShouldBeTrue();
    }

    [Fact]
    public void ShouldDenyStrangerWhenOnlyContactsAllowed()
    {
        Evaluate([Rule(PrivacyValueType.AllowContacts)], ContactType.None).ShouldBeFalse();
    }

    [Fact]
    public void ShouldAllowContactWhenOnlyContactsAllowed()
    {
        Evaluate([Rule(PrivacyValueType.AllowContacts)], ContactType.ContactOfTargetUser).ShouldBeTrue();
    }

    // --- allowPremium ---

    [Fact]
    public void ShouldAllowPremiumViewerWhenPremiumAllowed()
    {
        Evaluate([Rule(PrivacyValueType.AllowPremium)], ContactType.None,
            Context(isPremium: true)).ShouldBeTrue();
    }

    [Fact]
    public void ShouldDenyNonPremiumViewerWhenPremiumAllowed()
    {
        Evaluate([Rule(PrivacyValueType.AllowPremium)], ContactType.None,
            Context(isPremium: false)).ShouldBeFalse();
    }

    [Fact]
    public void ShouldDenyPremiumViewerWhenViewerContextUnknown()
    {
        // Callers that cannot supply viewer facts must not accidentally widen access.
        Evaluate([Rule(PrivacyValueType.AllowPremium)], ContactType.None).ShouldBeFalse();
    }

    // --- allowBots / disallowBots ---

    [Fact]
    public void ShouldAllowBotViewerWhenBotsAllowed()
    {
        Evaluate([Rule(PrivacyValueType.AllowBots)], ContactType.None,
            Context(isBot: true)).ShouldBeTrue();
    }

    [Fact]
    public void ShouldDenyBotViewerWhenBotsDisallowed()
    {
        var rules = new[]
        {
            Rule(PrivacyValueType.AllowAll),
            Rule(PrivacyValueType.DisallowBots)
        };

        Evaluate(rules, ContactType.None, Context(isBot: true)).ShouldBeFalse();
    }

    [Fact]
    public void ShouldNotApplyBotRuleToHumanViewer()
    {
        var rules = new[]
        {
            Rule(PrivacyValueType.AllowAll),
            Rule(PrivacyValueType.DisallowBots)
        };

        Evaluate(rules, ContactType.None, Context(isBot: false)).ShouldBeTrue();
    }

    // --- allowCloseFriends ---

    [Fact]
    public void ShouldAllowCloseFriend()
    {
        Evaluate([Rule(PrivacyValueType.AllowCloseFriends)], ContactType.None,
            Context(isCloseFriend: true)).ShouldBeTrue();
    }

    [Fact]
    public void ShouldDenyNonCloseFriend()
    {
        Evaluate([Rule(PrivacyValueType.AllowCloseFriends)], ContactType.ContactOfTargetUser,
            Context(isCloseFriend: false)).ShouldBeFalse();
    }

    // --- allowChatParticipants / disallowChatParticipants ---

    [Fact]
    public void ShouldAllowSharedChatParticipant()
    {
        Evaluate([Ids(PrivacyValueType.AllowChatParticipants, 555)], ContactType.None,
            Context(chatIds: [555])).ShouldBeTrue();
    }

    [Fact]
    public void ShouldDenyViewerFromUnlistedChat()
    {
        Evaluate([Ids(PrivacyValueType.AllowChatParticipants, 555)], ContactType.None,
            Context(chatIds: [777])).ShouldBeFalse();
    }

    [Fact]
    public void ShouldPreferDisallowChatParticipantsOverAllowAll()
    {
        var rules = new[]
        {
            Rule(PrivacyValueType.AllowAll),
            Ids(PrivacyValueType.DisallowChatParticipants, 555)
        };

        Evaluate(rules, ContactType.None, Context(chatIds: [555])).ShouldBeFalse();
    }

    [Fact]
    public void ShouldPreferExplicitUserRuleOverChatRule()
    {
        var rules = new[]
        {
            Ids(PrivacyValueType.DisallowChatParticipants, 555),
            Ids(PrivacyValueType.AllowUsers, ViewerId)
        };

        Evaluate(rules, ContactType.None, Context(chatIds: [555])).ShouldBeTrue();
    }

    // --- unsupported rules must fail closed ---

    [Fact]
    public void ShouldDenyWhenRuleKindIsNotUnderstood()
    {
        Evaluate([Rule(PrivacyValueType.Unknown)], ContactType.None).ShouldBeFalse();
    }

    // --- fact-dependent disallow rules must fail closed when the viewer facts are missing ---

    [Fact]
    public void ShouldDenyChatParticipantRuleWhenViewerFactsAreUnknown()
    {
        // "Everybody, except <chat>" — the standard Telegram exclusion. Without the viewer's chat
        // list, an unmatched disallowChatParticipants cannot be read as "the viewer is not in that
        // chat", so it must not fall through to the allowAll rule beside it.
        var rules = new[]
        {
            Rule(PrivacyValueType.AllowAll),
            Ids(PrivacyValueType.DisallowChatParticipants, 555)
        };

        Evaluate(rules, ContactType.None, PrivacyViewerContext.Unknown).ShouldBeFalse();
    }

    [Fact]
    public void ShouldDenyBotRuleWhenViewerFactsAreUnknown()
    {
        var rules = new[]
        {
            Rule(PrivacyValueType.AllowAll),
            Rule(PrivacyValueType.DisallowBots)
        };

        Evaluate(rules, ContactType.None, PrivacyViewerContext.Unknown).ShouldBeFalse();
    }

    [Fact]
    public void ShouldStillHonourExplicitAllowUsersWhenViewerFactsAreUnknown()
    {
        // The fail-closed branch must not override an explicit per-user grant, which is evaluated
        // from the request identity alone and needs none of the missing facts.
        var rules = new[]
        {
            Ids(PrivacyValueType.DisallowChatParticipants, 555),
            Ids(PrivacyValueType.AllowUsers, ViewerId)
        };

        Evaluate(rules, ContactType.None, PrivacyViewerContext.Unknown).ShouldBeTrue();
    }

    private static bool Evaluate(
        IReadOnlyList<PrivacyValueData> rules,
        ContactType contactType,
        PrivacyViewerContext? viewerContext = null)
    {
        var helper = new PrivacyHelper();
        var readModel = new FakePrivacyReadModel(rules);

        return helper.IsAllowedByPrivacy(ViewerId, readModel, contactType,
            viewerContext ?? PrivacyViewerContext.Unknown);
    }

    private static PrivacyViewerContext Context(
        bool isPremium = false,
        bool isBot = false,
        bool isCloseFriend = false,
        IEnumerable<long>? chatIds = null)
    {
        return new PrivacyViewerContext(isPremium, isBot, isCloseFriend,
            (chatIds ?? []).ToHashSet());
    }

    private static PrivacyValueData Rule(PrivacyValueType type) => new(type);

    private static PrivacyValueData Ids(PrivacyValueType type, params long[] ids) =>
        new(type, System.Text.Json.JsonSerializer.Serialize(ids.ToList()));

    private sealed class FakePrivacyReadModel(IReadOnlyList<PrivacyValueData> rules) : IPrivacyReadModel
    {
        public string Id => "test";
        public PrivacyType PrivacyType => PrivacyType.StatusTimestamp;
        public IReadOnlyList<PrivacyValueData> PrivacyValueDataList { get; } = rules;
        public long UserId => OtherId;
    }
}
