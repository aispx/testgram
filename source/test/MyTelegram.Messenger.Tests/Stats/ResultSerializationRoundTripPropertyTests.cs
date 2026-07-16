using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using StatsSchema = MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 19: Result objects serialize and round-trip completely.
///
/// For any valid request across all seven stats methods, the returned schema result object serializes to
/// bytes with every non-optional TL field populated and deserializes back to an equivalent object, with
/// omitted optional fields having their TL flag bit unset and their value absent.
///
/// Validates: Requirements 2.1, 2.2, 3.1, 3.2, 4.1, 5.1, 12.2, 12.3.
///
/// <para>The generator (<see cref="ResultSerializationArbitraries"/>) builds a fully-populated instance of
/// each of the six distinct result objects the seven methods return — <c>stats.broadcastStats</c>
/// (getBroadcastStats), <c>stats.megagroupStats</c> (getMegagroupStats), <c>stats.messageStats</c>
/// (getMessageStats), <c>stats.storyStats</c> (getStoryStats), <c>stats.publicForwards</c>
/// (getMessagePublicForwards + getStoryPublicForwards), and every <c>StatsGraph</c> variant returned by
/// loadAsyncGraph (<c>statsGraph</c>, <c>statsGraphAsync</c>, <c>statsGraphError</c>) — with every
/// non-optional TL field populated and field values varied per run. The optional fields that these result
/// objects carry (<c>publicForwards.next_offset</c>, <c>statsGraph.zoom_token</c>) are independently
/// toggled present/absent so the round-trip is exercised with the flag bit both set and unset
/// (Requirement 12.3).</para>
///
/// <para>The check is a full wire-level round-trip: the object is serialized to TL bytes
/// (<see cref="TlObjectExtensions.ToBytes"/>), deserialized back
/// (<see cref="TlObjectExtensions.ToTObject{TObject}(byte[])"/>), and the re-serialization of the
/// deserialized object is asserted byte-for-byte equal to the original bytes. Because serialization writes
/// every declared non-optional field, a missing (null) non-optional field would fault the serialize step;
/// the per-kind assertions additionally pin that each non-optional field survived as a populated value and
/// that omitted optionals came back absent. Each run covers a minimum of 100 generated cases.</para>
/// </summary>
[Properties(Arbitrary = new[] { typeof(ResultSerializationArbitraries) }, MaxTest = 100)]
public class ResultSerializationRoundTripPropertyTests
{
    [Property]
    public void Result_objects_serialize_and_round_trip_completely(StatsResultFixture fixture)
    {
        // Serialize the produced result object to TL bytes. A null non-optional field would fault here,
        // so a successful serialization already witnesses "every non-optional field populated"
        // (Requirement 12.2).
        var bytes = fixture.Result.ToBytes();
        bytes.ShouldNotBeNull($"{fixture.Method} result must serialize to bytes");

        // Deserialize back into an object of the same constructor.
        var roundTripped = fixture.Deserialize(bytes!);
        roundTripped.ShouldNotBeNull($"{fixture.Method} result must deserialize back to an object");

        // Every non-optional field must have come back populated, and every omitted optional absent.
        fixture.AssertRoundTripped(roundTripped!);

        // Wire-level equivalence: re-serializing the deserialized object reproduces the original bytes.
        // This proves columns/values/flags — including unset optional flag bits — round-tripped exactly.
        roundTripped!.ToBytes().ShouldBe(bytes, $"{fixture.Method} result must round-trip byte-for-byte");
    }
}

/// <summary>
/// Carries one generated stats result object together with the constructor-specific deserialize step and
/// the per-constructor "non-optional populated / optional absent" assertions used by
/// <see cref="ResultSerializationRoundTripPropertyTests"/>.
/// </summary>
public sealed class StatsResultFixture
{
    public required string Method { get; init; }
    public required IObject Result { get; init; }
    public required Func<byte[], IObject> Deserialize { get; init; }
    public required Action<IObject> AssertRoundTripped { get; init; }

    public override string ToString() => Method;
}

/// <summary>
/// FsCheck arbitraries for <see cref="StatsResultFixture"/> — reference with
/// <c>[Properties(Arbitrary = new[] { typeof(ResultSerializationArbitraries) })]</c>.
/// </summary>
public static class ResultSerializationArbitraries
{
    public static Arbitrary<StatsResultFixture> StatsResult() => Arb.From(ResultSerializationGen.StatsResult);
}

/// <summary>
/// Generators that build fully-populated stats result objects (with varied field values) for the
/// round-trip property. Composed from the shared <see cref="StatsGen"/> primitives.
/// </summary>
internal static class ResultSerializationGen
{
    // ---- Scalar / leaf generators ------------------------------------------------------------

    private static Gen<double> Number => Gen.Choose(0, 100_000_000).Select(i => i / 13.0);

    private static Gen<int> Count => Gen.Choose(0, 1_000_000);

    private static Gen<long> LongId => Gen.Choose(1, 1_000_000).Select(i => (long)i);

    // A pool of strings covering empty, ASCII, non-ASCII, JSON-ish, and emoji text so string fields are
    // exercised across code paths.
    private static Gen<string> Text => Gen.Elements(
        "",
        "abc",
        "統計データ",
        "graph 🚀 token",
        "{\"columns\":[[\"x\",1690848000000]],\"types\":{\"x\":\"x\"}}",
        "next_offset_42");

    private static Gen<string?> OptionalText =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<string?>(null)),
            Tuple.Create(2, Text.Select(s => (string?)s)));

    // ---- Composite leaf generators -----------------------------------------------------------

    private static Gen<MyTelegram.Schema.IStatsDateRangeDays> Period =>
        from a in Gen.Choose(0, int.MaxValue / 2)
        from b in Gen.Choose(0, int.MaxValue / 2)
        select (MyTelegram.Schema.IStatsDateRangeDays)new TStatsDateRangeDays
        {
            MinDate = Math.Min(a, b),
            MaxDate = Math.Max(a, b)
        };

    private static Gen<MyTelegram.Schema.IStatsAbsValueAndPrev> Abs =>
        from c in Number
        from p in Number
        select (MyTelegram.Schema.IStatsAbsValueAndPrev)new TStatsAbsValueAndPrev { Current = c, Previous = p };

    private static Gen<MyTelegram.Schema.IStatsPercentValue> Percent =>
        from part in Number
        from total in Number
        select (MyTelegram.Schema.IStatsPercentValue)new TStatsPercentValue { Part = part, Total = total };

    // An inline statsGraph with the optional zoom_token independently present/absent (Requirement 12.3).
    private static Gen<MyTelegram.Schema.IStatsGraph> InlineGraph =>
        from data in Text
        from zoom in OptionalText
        select (MyTelegram.Schema.IStatsGraph)new TStatsGraph
        {
            Json = new TDataJSON { Data = data },
            ZoomToken = zoom
        };

    private static Gen<MyTelegram.Schema.IPostInteractionCounters> PostInteraction =>
        Gen.OneOf(
            from id in Count
            from v in Count
            from f in Count
            from r in Count
            select (MyTelegram.Schema.IPostInteractionCounters)new TPostInteractionCountersMessage
            {
                MsgId = id,
                Views = v,
                Forwards = f,
                Reactions = r
            },
            from id in Count
            from v in Count
            from f in Count
            from r in Count
            select (MyTelegram.Schema.IPostInteractionCounters)new TPostInteractionCountersStory
            {
                StoryId = id,
                Views = v,
                Forwards = f,
                Reactions = r
            });

    private static Gen<MyTelegram.Schema.IStatsGroupTopPoster> TopPoster =>
        from u in LongId
        from m in Count
        from a in Count
        select (MyTelegram.Schema.IStatsGroupTopPoster)new TStatsGroupTopPoster { UserId = u, Messages = m, AvgChars = a };

    private static Gen<MyTelegram.Schema.IStatsGroupTopAdmin> TopAdmin =>
        from u in LongId
        from d in Count
        from k in Count
        from b in Count
        select (MyTelegram.Schema.IStatsGroupTopAdmin)new TStatsGroupTopAdmin { UserId = u, Deleted = d, Kicked = k, Banned = b };

    private static Gen<MyTelegram.Schema.IStatsGroupTopInviter> TopInviter =>
        from u in LongId
        from i in Count
        select (MyTelegram.Schema.IStatsGroupTopInviter)new TStatsGroupTopInviter { UserId = u, Invitations = i };

    private static Gen<MyTelegram.Schema.IUser> User =>
        from id in LongId
        select (MyTelegram.Schema.IUser)new TUserEmpty { Id = id };

    private static Gen<MyTelegram.Schema.IChat> Chat =>
        from id in LongId
        select (MyTelegram.Schema.IChat)new TChatEmpty { Id = id };

    private static Gen<MyTelegram.Schema.IPublicForward> PublicForward =>
        Gen.OneOf(
            from id in Count
            select (MyTelegram.Schema.IPublicForward)new TPublicForwardMessage { Message = new TMessageEmpty { Id = id } },
            from cid in LongId
            from sid in Count
            select (MyTelegram.Schema.IPublicForward)new TPublicForwardStory
            {
                Peer = new TPeerChannel { ChannelId = cid },
                Story = new TStoryItemDeleted { Id = sid }
            });

    // ---- Vector helper -----------------------------------------------------------------------

    private static Gen<TVector<T>> VectorOf<T>(Gen<T> element, int min = 0, int max = 4) =>
        from n in Gen.Choose(min, max)
        from items in StatsGen.ArrayOfLength(n, element)
        select new TVector<T>(items);

    // ---- Result-object generators (one per distinct result type) -----------------------------

    private static Gen<StatsResultFixture> BroadcastStats =>
        from period in Period
        from abs in StatsGen.ArrayOfLength(7, Abs)
        from notif in Percent
        from graphs in StatsGen.ArrayOfLength(12, InlineGraph)
        from recent in VectorOf(PostInteraction)
        select new StatsResultFixture
        {
            Method = "stats.getBroadcastStats -> stats.broadcastStats",
            Result = new StatsSchema.TBroadcastStats
            {
                Period = period,
                Followers = abs[0],
                ViewsPerPost = abs[1],
                SharesPerPost = abs[2],
                ReactionsPerPost = abs[3],
                ViewsPerStory = abs[4],
                SharesPerStory = abs[5],
                ReactionsPerStory = abs[6],
                EnabledNotifications = notif,
                GrowthGraph = graphs[0],
                FollowersGraph = graphs[1],
                MuteGraph = graphs[2],
                TopHoursGraph = graphs[3],
                InteractionsGraph = graphs[4],
                IvInteractionsGraph = graphs[5],
                ViewsBySourceGraph = graphs[6],
                NewFollowersBySourceGraph = graphs[7],
                LanguagesGraph = graphs[8],
                ReactionsByEmotionGraph = graphs[9],
                StoryInteractionsGraph = graphs[10],
                StoryReactionsByEmotionGraph = graphs[11],
                RecentPostsInteractions = recent
            },
            Deserialize = bytes => bytes.AsMemory().ToTObject<StatsSchema.TBroadcastStats>(),
            AssertRoundTripped = obj =>
            {
                var s = (StatsSchema.TBroadcastStats)obj;
                s.Period.ShouldNotBeNull();
                s.Followers.ShouldNotBeNull();
                s.ViewsPerPost.ShouldNotBeNull();
                s.SharesPerPost.ShouldNotBeNull();
                s.ReactionsPerPost.ShouldNotBeNull();
                s.ViewsPerStory.ShouldNotBeNull();
                s.SharesPerStory.ShouldNotBeNull();
                s.ReactionsPerStory.ShouldNotBeNull();
                s.EnabledNotifications.ShouldNotBeNull();
                s.GrowthGraph.ShouldNotBeNull();
                s.FollowersGraph.ShouldNotBeNull();
                s.MuteGraph.ShouldNotBeNull();
                s.TopHoursGraph.ShouldNotBeNull();
                s.InteractionsGraph.ShouldNotBeNull();
                s.IvInteractionsGraph.ShouldNotBeNull();
                s.ViewsBySourceGraph.ShouldNotBeNull();
                s.NewFollowersBySourceGraph.ShouldNotBeNull();
                s.LanguagesGraph.ShouldNotBeNull();
                s.ReactionsByEmotionGraph.ShouldNotBeNull();
                s.StoryInteractionsGraph.ShouldNotBeNull();
                s.StoryReactionsByEmotionGraph.ShouldNotBeNull();
                s.RecentPostsInteractions.ShouldNotBeNull();
            }
        };

    private static Gen<StatsResultFixture> MegagroupStats =>
        from period in Period
        from abs in StatsGen.ArrayOfLength(4, Abs)
        from graphs in StatsGen.ArrayOfLength(8, InlineGraph)
        from posters in VectorOf(TopPoster)
        from admins in VectorOf(TopAdmin)
        from inviters in VectorOf(TopInviter)
        from users in VectorOf(User)
        select new StatsResultFixture
        {
            Method = "stats.getMegagroupStats -> stats.megagroupStats",
            Result = new StatsSchema.TMegagroupStats
            {
                Period = period,
                Members = abs[0],
                Messages = abs[1],
                Viewers = abs[2],
                Posters = abs[3],
                GrowthGraph = graphs[0],
                MembersGraph = graphs[1],
                NewMembersBySourceGraph = graphs[2],
                LanguagesGraph = graphs[3],
                MessagesGraph = graphs[4],
                ActionsGraph = graphs[5],
                TopHoursGraph = graphs[6],
                WeekdaysGraph = graphs[7],
                TopPosters = posters,
                TopAdmins = admins,
                TopInviters = inviters,
                Users = users
            },
            Deserialize = bytes => bytes.AsMemory().ToTObject<StatsSchema.TMegagroupStats>(),
            AssertRoundTripped = obj =>
            {
                var s = (StatsSchema.TMegagroupStats)obj;
                s.Period.ShouldNotBeNull();
                s.Members.ShouldNotBeNull();
                s.Messages.ShouldNotBeNull();
                s.Viewers.ShouldNotBeNull();
                s.Posters.ShouldNotBeNull();
                s.GrowthGraph.ShouldNotBeNull();
                s.MembersGraph.ShouldNotBeNull();
                s.NewMembersBySourceGraph.ShouldNotBeNull();
                s.LanguagesGraph.ShouldNotBeNull();
                s.MessagesGraph.ShouldNotBeNull();
                s.ActionsGraph.ShouldNotBeNull();
                s.TopHoursGraph.ShouldNotBeNull();
                s.WeekdaysGraph.ShouldNotBeNull();
                s.TopPosters.ShouldNotBeNull();
                s.TopAdmins.ShouldNotBeNull();
                s.TopInviters.ShouldNotBeNull();
                s.Users.ShouldNotBeNull();
            }
        };

    private static Gen<StatsResultFixture> MessageStats =>
        from views in InlineGraph
        from reactions in InlineGraph
        select new StatsResultFixture
        {
            Method = "stats.getMessageStats -> stats.messageStats",
            Result = new StatsSchema.TMessageStats { ViewsGraph = views, ReactionsByEmotionGraph = reactions },
            Deserialize = bytes => bytes.AsMemory().ToTObject<StatsSchema.TMessageStats>(),
            AssertRoundTripped = obj =>
            {
                var s = (StatsSchema.TMessageStats)obj;
                s.ViewsGraph.ShouldNotBeNull();
                s.ReactionsByEmotionGraph.ShouldNotBeNull();
            }
        };

    private static Gen<StatsResultFixture> StoryStats =>
        from views in InlineGraph
        from reactions in InlineGraph
        select new StatsResultFixture
        {
            Method = "stats.getStoryStats -> stats.storyStats",
            Result = new StatsSchema.TStoryStats { ViewsGraph = views, ReactionsByEmotionGraph = reactions },
            Deserialize = bytes => bytes.AsMemory().ToTObject<StatsSchema.TStoryStats>(),
            AssertRoundTripped = obj =>
            {
                var s = (StatsSchema.TStoryStats)obj;
                s.ViewsGraph.ShouldNotBeNull();
                s.ReactionsByEmotionGraph.ShouldNotBeNull();
            }
        };

    private static Gen<StatsResultFixture> PublicForwards =>
        // Covers both getMessagePublicForwards and getStoryPublicForwards (same result constructor).
        from method in Gen.Elements("stats.getMessagePublicForwards", "stats.getStoryPublicForwards")
        from count in Count
        from forwards in VectorOf(PublicForward)
        from nextOffset in OptionalText
        from chats in VectorOf(Chat)
        from users in VectorOf(User)
        select new StatsResultFixture
        {
            Method = method + " -> stats.publicForwards",
            Result = new StatsSchema.TPublicForwards
            {
                Count = count,
                Forwards = forwards,
                NextOffset = nextOffset,
                Chats = chats,
                Users = users
            },
            Deserialize = bytes => bytes.AsMemory().ToTObject<StatsSchema.TPublicForwards>(),
            AssertRoundTripped = obj =>
            {
                var s = (StatsSchema.TPublicForwards)obj;
                s.Forwards.ShouldNotBeNull();
                s.Chats.ShouldNotBeNull();
                s.Users.ShouldNotBeNull();
                // The optional next_offset round-trips present exactly when it was populated (Requirement 12.3).
                (s.NextOffset != null).ShouldBe(nextOffset != null);
                s.Flags.IsBitSet(0).ShouldBe(nextOffset != null);
            }
        };

    private static Gen<StatsResultFixture> LoadAsyncGraphInline =>
        from graph in InlineGraph
        select new StatsResultFixture
        {
            Method = "stats.loadAsyncGraph -> statsGraph",
            Result = graph,
            Deserialize = bytes => bytes.AsMemory().ToTObject<TStatsGraph>(),
            AssertRoundTripped = obj =>
            {
                var g = (TStatsGraph)obj;
                g.Json.ShouldNotBeNull();
                var expectedZoom = ((TStatsGraph)graph).ZoomToken;
                (g.ZoomToken != null).ShouldBe(expectedZoom != null);
                g.Flags.IsBitSet(0).ShouldBe(expectedZoom != null);
            }
        };

    private static Gen<StatsResultFixture> LoadAsyncGraphAsync =>
        from token in StatsGen.OpaqueToken
        select new StatsResultFixture
        {
            Method = "stats.loadAsyncGraph -> statsGraphAsync",
            Result = new TStatsGraphAsync { Token = token },
            Deserialize = bytes => bytes.AsMemory().ToTObject<TStatsGraphAsync>(),
            AssertRoundTripped = obj => ((TStatsGraphAsync)obj).Token.ShouldNotBeNull()
        };

    private static Gen<StatsResultFixture> LoadAsyncGraphError =>
        from error in Text
        select new StatsResultFixture
        {
            Method = "stats.loadAsyncGraph -> statsGraphError",
            Result = new TStatsGraphError { Error = error },
            Deserialize = bytes => bytes.AsMemory().ToTObject<TStatsGraphError>(),
            AssertRoundTripped = obj => ((TStatsGraphError)obj).Error.ShouldNotBeNull()
        };

    /// <summary>
    /// Picks uniformly among the result objects of all seven methods (with loadAsyncGraph split across its
    /// three graph constructors) so a single property covers every returned schema result type.
    /// </summary>
    public static Gen<StatsResultFixture> StatsResult =>
        Gen.OneOf(
            BroadcastStats,
            MegagroupStats,
            MessageStats,
            StoryStats,
            PublicForwards,
            LoadAsyncGraphInline,
            LoadAsyncGraphAsync,
            LoadAsyncGraphError);
}
