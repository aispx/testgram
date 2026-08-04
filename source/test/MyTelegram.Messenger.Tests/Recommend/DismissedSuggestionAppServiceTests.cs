using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Recommend;

/// <summary>
/// Integration tests for <see cref="DismissedSuggestionAppService"/>, which backs
/// <c>help.dismissSuggestion</c> — the dismissal has to outlive the client session, so what is
/// actually persisted is the whole of the behaviour under test.
/// See https://corefork.telegram.org/api/config#suggestions
/// </summary>
public class DismissedSuggestionAppServiceTests
{
    [RequiresMongoDbFact]
    public async Task A_dismissed_suggestion_is_remembered_for_that_user_only()
    {
        using var mongo = EmbeddedMongoServer.Start();
        IDismissedSuggestionAppService service = new DismissedSuggestionAppService(mongo.Database);

        await service.DismissAsync(selfUserId: 1, peer: null, suggestion: "AUTOARCHIVE_POPULAR");

        (await service.GetDismissedAsync(1)).ShouldBe(["AUTOARCHIVE_POPULAR"]);
        (await service.GetDismissedAsync(2)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Dismissing_the_same_suggestion_twice_is_idempotent()
    {
        using var mongo = EmbeddedMongoServer.Start();
        IDismissedSuggestionAppService service = new DismissedSuggestionAppService(mongo.Database);

        // Clients re-send the RPC on retry; a duplicate key must not blow up or double-count.
        await service.DismissAsync(1, null, "VALIDATE_PHONE_NUMBER");
        await service.DismissAsync(1, null, "VALIDATE_PHONE_NUMBER");

        (await service.GetDismissedAsync(1)).ShouldBe(["VALIDATE_PHONE_NUMBER"]);
        var count = await mongo.Database
            .GetCollection<BsonDocument>("dismissed_suggestions")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Channel_scoped_and_global_dismissals_are_kept_apart()
    {
        using var mongo = EmbeddedMongoServer.Start();
        IDismissedSuggestionAppService service = new DismissedSuggestionAppService(mongo.Database);

        var channel = new Peer(PeerType.Channel, 777);
        await service.DismissAsync(1, channel, "CONVERT_GIGAGROUP");

        // channelFull carries its own pending_suggestions, so a per-channel dismissal must not
        // suppress the global list and vice versa.
        (await service.GetDismissedAsync(1, channel)).ShouldBe(["CONVERT_GIGAGROUP"]);
        (await service.GetDismissedAsync(1)).ShouldBeEmpty();
        (await service.GetDismissedAsync(1, new Peer(PeerType.Channel, 778))).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Filtering_drops_dismissed_entries_and_preserves_order()
    {
        using var mongo = EmbeddedMongoServer.Start();
        IDismissedSuggestionAppService service = new DismissedSuggestionAppService(mongo.Database);

        await service.DismissAsync(1, null, "SETUP_PASSWORD");

        var remaining = await service.FilterDismissedAsync(1, ["NEWCOMER_TICKS", "SETUP_PASSWORD", "PREMIUM_ANNUAL"]);

        remaining.ShouldBe(["NEWCOMER_TICKS", "PREMIUM_ANNUAL"]);
    }
}
