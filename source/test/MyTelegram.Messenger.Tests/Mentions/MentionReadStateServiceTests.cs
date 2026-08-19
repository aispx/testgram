using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Services.Mentions;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Mentions;

/// <summary>
/// Feature: mentions — which mentions of a user in a dialog are still unread.
///
/// <para>
/// The dialog's <c>unread_mentions_count</c> is event-sourced, but whether an individual message still
/// shows its @ badge is decided here: <c>messages.readMentions</c> moves a watermark, while
/// <c>messages.readMessageContents</c> and a topic-scoped read mark single ids. These tests run against
/// a real <c>mongod</c> because the behaviour under test is the upsert and the pruning, both of which
/// live in the database. See https://corefork.telegram.org/api/mentions
/// </para>
/// </summary>
public class MentionReadStateServiceTests
{
    private const long UserId = 100;
    private static readonly Peer Channel = new(PeerType.Channel, 800000000001);

    [RequiresMongoDbFact]
    public async Task An_unknown_dialog_has_every_mention_unread()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        var state = await service.GetAsync(UserId, Channel);

        state.ShouldBeNull();
        IMentionReadStateService.IsUnread(state, 1).ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task The_watermark_reads_everything_up_to_it_and_nothing_above()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        await service.MarkAllReadAsync(UserId, Channel, 10);

        var state = await service.GetAsync(UserId, Channel);
        IMentionReadStateService.IsUnread(state, 9).ShouldBeFalse();
        IMentionReadStateService.IsUnread(state, 10).ShouldBeFalse();

        // A mention arriving after the read has to light the badge up again.
        IMentionReadStateService.IsUnread(state, 11).ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task The_watermark_only_moves_forward()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        await service.MarkAllReadAsync(UserId, Channel, 10);
        // Two sessions reading at once must not walk the pointer back.
        await service.MarkAllReadAsync(UserId, Channel, 4);

        var state = await service.GetAsync(UserId, Channel);
        state!.ReadMaxId.ShouldBe(10);
    }

    [RequiresMongoDbFact]
    public async Task Single_mentions_are_read_one_by_one_above_the_watermark()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        await service.MarkAllReadAsync(UserId, Channel, 10);
        await service.MarkReadAsync(UserId, Channel, [12, 14]);

        var state = await service.GetAsync(UserId, Channel);
        IMentionReadStateService.IsUnread(state, 12).ShouldBeFalse();
        IMentionReadStateService.IsUnread(state, 14).ShouldBeFalse();
        IMentionReadStateService.IsUnread(state, 13).ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task Ids_already_covered_by_the_watermark_are_not_stored()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        await service.MarkAllReadAsync(UserId, Channel, 10);
        await service.MarkReadAsync(UserId, Channel, [3, 7]);

        var state = await service.GetAsync(UserId, Channel);
        state!.ReadIds.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Moving_the_watermark_prunes_the_ids_it_covers()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        await service.MarkReadAsync(UserId, Channel, [3, 12]);
        await service.MarkAllReadAsync(UserId, Channel, 10);

        var state = await service.GetAsync(UserId, Channel);
        state!.ReadMaxId.ShouldBe(10);
        state.ReadIds.ShouldBe([12]);
    }

    [RequiresMongoDbFact]
    public async Task Read_state_is_scoped_to_one_user_and_one_dialog()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());

        await service.MarkAllReadAsync(UserId, Channel, 10);

        (await service.GetAsync(UserId + 1, Channel)).ShouldBeNull();
        (await service.GetAsync(UserId, new Peer(PeerType.Channel, Channel.PeerId + 1))).ShouldBeNull();
        // A chat and a channel with the same numeric id are different dialogs.
        (await service.GetAsync(UserId, new Peer(PeerType.Chat, Channel.PeerId))).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task The_batch_lookup_keys_states_by_peer()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new MentionReadStateService(mongo.Database, Mock.Of<IQueryProcessor>());
        var otherChannel = new Peer(PeerType.Channel, Channel.PeerId + 1);

        await service.MarkAllReadAsync(UserId, Channel, 10);
        await service.MarkAllReadAsync(UserId, otherChannel, 20);

        var states = await service.GetManyAsync(UserId, [Channel, otherChannel]);

        states[IMentionReadStateService.Key(Channel)].ReadMaxId.ShouldBe(10);
        states[IMentionReadStateService.Key(otherChannel)].ReadMaxId.ShouldBe(20);
    }
}
