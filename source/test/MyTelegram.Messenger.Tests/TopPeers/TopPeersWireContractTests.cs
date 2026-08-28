using MyTelegram.Messenger.Services.Hashing;
using MyTelegram.Messenger.Services.TopPeers;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.TopPeers;

/// <summary>
/// Feature: the parts of <c>contacts.getTopPeers</c> that a client computes for itself and quotes back,
/// so the server has no freedom in them.
/// See https://corefork.telegram.org/api/top-rating
/// </summary>
public class TopPeersWireContractTests
{
    [Fact]
    public void The_category_order_is_the_one_tdlib_hashes_in()
    {
        // tdlib asks for every category at once and folds get_vector_hash over the ids of all of its
        // cached categories concatenated in TopDialogCategory enum order, so emitting them in any other
        // order makes its hash unmatchable and topPeersNotModified unreachable.
        TopPeerCategoryHelper.WireOrder.ShouldBe([
            TopPeerCategory.Correspondents,
            TopPeerCategory.BotsPM,
            TopPeerCategory.BotsInline,
            TopPeerCategory.Groups,
            TopPeerCategory.Channels,
            TopPeerCategory.PhoneCalls,
            TopPeerCategory.ForwardUsers,
            TopPeerCategory.ForwardChats,
            TopPeerCategory.BotsApp
        ]);
    }

    [Fact]
    public void Every_category_survives_a_round_trip_through_the_wire_constructors()
    {
        foreach (var category in TopPeerCategoryHelper.WireOrder)
        {
            TopPeerCategoryHelper.FromTl(TopPeerCategoryHelper.ToTl(category)).ShouldBe(category);
        }
    }

    [Fact]
    public void A_category_this_layer_does_not_model_resolves_to_no_category()
    {
        // Which resetTopPeerRating treats as "every category" rather than as an error.
        TopPeerCategoryHelper.FromTl(null).ShouldBeNull();
    }

    [Fact]
    public void The_hash_is_the_unsigned_fold_over_the_bare_peer_ids_in_wire_order()
    {
        var categories = new List<ITopPeerCategoryPeers>
        {
            Category(TopPeerCategory.Correspondents, new TPeerUser { UserId = 111 },
                new TPeerUser { UserId = 222 }),
            Category(TopPeerCategory.Channels, new TPeerChannel { ChannelId = 333 })
        };

        // tdesktop pushes peerToUser(...).bare and tdlib pushes dialog_id.get_channel_id().get(): the
        // bare id, never a marked dialog id.
        TopPeersHashHelper.ComputeHash(categories)
            .ShouldBe(VectorHashHelper.ComputeHash([111L, 222L, 333L]));
    }

    [Fact]
    public void Reordering_the_peers_changes_the_hash()
    {
        var forward = TopPeersHashHelper.ComputeHash([
            Category(TopPeerCategory.Correspondents, new TPeerUser { UserId = 111 },
                new TPeerUser { UserId = 222 })
        ]);
        var reversed = TopPeersHashHelper.ComputeHash([
            Category(TopPeerCategory.Correspondents, new TPeerUser { UserId = 222 },
                new TPeerUser { UserId = 111 })
        ]);

        forward.ShouldNotBe(reversed);
    }

    [Fact]
    public void An_empty_answer_hashes_to_zero()
    {
        // The value a client with nothing cached sends, so the handler must not answer notModified to it.
        TopPeersHashHelper.ComputeHash([]).ShouldBe(0);
    }

    [Fact]
    public void The_signed_accumulator_is_not_interchangeable_with_the_unsigned_one()
    {
        // Why this hash is not computed with MessageSearchMongoHelper.CalcHash: with real ids the
        // accumulator goes negative within a couple of peers and the two disagree from then on.
        var ids = new[] { 2010001L, 2010002L, 2010003L, 2010004L };

        var signed = ids.Aggregate(0L, (current, id) =>
        {
            current ^= current >> 21;
            current ^= current << 35;
            current ^= current >> 4;

            return current + id;
        });

        VectorHashHelper.ComputeHash(ids).ShouldNotBe(signed);
    }

    private static ITopPeerCategoryPeers Category(TopPeerCategory category, params IPeer[] peers)
    {
        return new TTopPeerCategoryPeers
        {
            Category = TopPeerCategoryHelper.ToTl(category),
            Count = peers.Length,
            Peers = new TVector<ITopPeer>(peers.Select(p => (ITopPeer)new TTopPeer { Peer = p, Rating = 1 }))
        };
    }
}
