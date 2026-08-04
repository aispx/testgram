using MyTelegram.Domain.Aggregates.User;
using MyTelegram.Messenger.Handlers.LatestLayer.Account;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.EmojiStatuses;

/// <summary>
/// Unit tests for the server side of
/// <a href="https://core.telegram.org/api/emoji-status">emoji statuses</a>.
///
/// These pin down the parts that were previously wrong: the recent list grew duplicate copies of the
/// same emoji, the <a href="https://core.telegram.org/api/offsets#hash-generation">hash</a> used a
/// homegrown formula so <c>emojiStatusesNotModified</c> could never fire, and an expired
/// <c>until</c> was ignored so a status kept being advertised forever.
/// </summary>
public class EmojiStatusesHelperTests
{
    [Fact]
    public void Hash_of_an_empty_list_is_zero()
    {
        EmojiStatusesHelper.CalculateHash([]).ShouldBe(0);
    }

    [Fact]
    public void Hash_depends_on_the_order_of_the_statuses()
    {
        // The recent list is ordered most-recent-first, so reordering it is a real change the client
        // has to be told about.
        var first = EmojiStatusesHelper.CalculateHash([1, 2, 3]);
        var second = EmojiStatusesHelper.CalculateHash([3, 2, 1]);

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Hash_is_stable_for_the_same_list()
    {
        EmojiStatusesHelper.CalculateHash([10, 20, 30])
            .ShouldBe(EmojiStatusesHelper.CalculateHash([10, 20, 30]));
    }

    [Fact]
    public void A_matching_hash_yields_not_modified()
    {
        var documentIds = new List<long> { 5, 6, 7 };
        var hash = EmojiStatusesHelper.CalculateHash(documentIds);

        var result = EmojiStatusesHelper.ToEmojiStatuses(documentIds, hash);

        result.ShouldBeOfType<MyTelegram.Schema.Account.TEmojiStatusesNotModified>();
    }

    [Fact]
    public void A_stale_hash_yields_the_full_list_with_the_new_hash()
    {
        var documentIds = new List<long> { 5, 6, 7 };

        var result = EmojiStatusesHelper.ToEmojiStatuses(documentIds, requestHash: 42);

        var statuses = result.ShouldBeOfType<MyTelegram.Schema.Account.TEmojiStatuses>();
        statuses.Hash.ShouldBe(EmojiStatusesHelper.CalculateHash(documentIds));
        statuses.Statuses.Count.ShouldBe(3);
        statuses.Statuses.Select(p => ((TEmojiStatus)p).DocumentId).ShouldBe(documentIds);
    }

    [Fact]
    public void A_zero_hash_always_yields_the_full_list()
    {
        // Clients send hash=0 on a cold start, and an empty list also hashes to 0 — that must not be
        // mistaken for "nothing changed".
        var result = EmojiStatusesHelper.ToEmojiStatuses([], requestHash: 0);

        result.ShouldBeOfType<MyTelegram.Schema.Account.TEmojiStatuses>()
            .Statuses.Count.ShouldBe(0);
    }
}

/// <summary>
/// The recent-status bookkeeping done by <see cref="UserState"/> when applying
/// <c>UserEmojiStatusUpdatedEvent</c>.
/// </summary>
public class UserStateEmojiStatusTests
{
    [Fact]
    public void Setting_a_status_records_it_as_the_most_recent_one()
    {
        var state = new UserState();

        Apply(state, new EmojiStatus(111));

        state.EmojiStatusDocumentId.ShouldBe(111);
        state.RecentEmojiStatus.ShouldBe([111]);
    }

    [Fact]
    public void Re_picking_a_status_moves_it_up_instead_of_duplicating_it()
    {
        var state = new UserState();

        Apply(state, new EmojiStatus(1));
        Apply(state, new EmojiStatus(2));
        Apply(state, new EmojiStatus(1));

        state.RecentEmojiStatus.ShouldBe([1, 2]);
    }

    [Fact]
    public void The_recent_list_is_capped()
    {
        var state = new UserState();

        foreach (var documentId in Enumerable.Range(1, UserState.MaxRecentEmojiStatuses + 5))
        {
            Apply(state, new EmojiStatus(documentId));
        }

        state.RecentEmojiStatus.Count.ShouldBe(UserState.MaxRecentEmojiStatuses);
        state.RecentEmojiStatus[0].ShouldBe(UserState.MaxRecentEmojiStatuses + 5);
    }

    [Fact]
    public void Clearing_the_status_keeps_the_recent_list()
    {
        // emojiStatusEmpty only removes the current status; the recently used ones stay available in
        // the picker until account.clearRecentEmojiStatuses is called.
        var state = new UserState();

        Apply(state, new EmojiStatus(7));
        Apply(state, null);

        state.EmojiStatusDocumentId.ShouldBeNull();
        state.EmojiStatusValidUntil.ShouldBeNull();
        state.EmojiStatusCollectibleId.ShouldBeNull();
        state.RecentEmojiStatus.ShouldBe([7]);
    }

    [Fact]
    public void A_collectible_status_keeps_its_collectible_id()
    {
        // Without this the profile decoration (title, slug, pattern, colors) is lost and the client
        // only sees a plain custom emoji.
        var state = new UserState();

        Apply(state, new EmojiStatus(500, Until: null, CollectibleId: 900));

        state.EmojiStatusCollectibleId.ShouldBe(900);
    }

    [Fact]
    public void Clearing_the_recent_statuses_empties_the_list_but_keeps_the_current_status()
    {
        var state = new UserState();

        Apply(state, new EmojiStatus(3));
        state.Apply(new UserRecentEmojiStatusesClearedEvent(RequestInfo.Empty, 1));

        state.RecentEmojiStatus.ShouldBeEmpty();
        state.EmojiStatusDocumentId.ShouldBe(3);
    }

    private static void Apply(UserState state, EmojiStatus? emojiStatus)
    {
        state.Apply(new UserEmojiStatusUpdatedEvent(RequestInfo.Empty, 1, emojiStatus));
    }
}
