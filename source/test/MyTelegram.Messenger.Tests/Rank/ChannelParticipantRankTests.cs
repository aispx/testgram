using Moq;
using MyTelegram.Converters.Mappers.LatestLayer;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Rank;

/// <summary>
/// Feature: channelParticipant.rank — the tag of a member returned by channels.getParticipant(s).
///
/// <para>
/// Ordinary members carry a tag just like admins do, so it has to survive the mapping from the member
/// read model. An empty tag means "no tag" and must leave the flag unset, otherwise clients render an
/// empty badge instead of nothing. See https://corefork.telegram.org/api/rank
/// </para>
/// </summary>
public class ChannelParticipantRankTests
{
    [Fact]
    public void An_ordinary_member_keeps_their_tag()
    {
        var participant = new ChannelParticipantMapper().Map(Member("designer"));

        participant.Rank.ShouldBe("designer");
    }

    [Fact]
    public void An_ordinary_member_without_a_tag_has_no_rank_flag()
    {
        new ChannelParticipantMapper().Map(Member(null)).Rank.ShouldBeNull();
        new ChannelParticipantMapper().Map(Member(string.Empty)).Rank.ShouldBeNull();
    }

    [Fact]
    public void The_self_participant_keeps_their_tag()
    {
        var participant = new ChannelParticipantSelfMapper().Map(Member("designer"));

        participant.Rank.ShouldBe("designer");
    }

    [Fact]
    public void The_self_participant_without_a_tag_has_no_rank_flag()
    {
        new ChannelParticipantSelfMapper().Map(Member(string.Empty)).Rank.ShouldBeNull();
    }

    private static IChannelMemberReadModel Member(string? rank)
    {
        var member = new Mock<IChannelMemberReadModel>();
        member.SetupGet(p => p.UserId).Returns(2_010_001);
        member.SetupGet(p => p.Date).Returns(1_690_848_000);
        member.SetupGet(p => p.Rank).Returns(rank);

        return member.Object;
    }
}
