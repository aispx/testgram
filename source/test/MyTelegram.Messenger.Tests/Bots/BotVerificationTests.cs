using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Bots;

/// <summary>
/// Feature: <c>bots.setCustomVerification</c>, the write half of
/// <a href="https://corefork.telegram.org/api/bots/verification">third-party verification</a>.
///
/// <para>
/// The badge is rendered with the icon and the name of the organisation behind the bot, so who may
/// issue one is the whole point of the method: only a bot the operator authorised through
/// <c>bot-verifier-settings</c>, and only the bot that issued a badge may revoke it. The peer also
/// has to be one that can actually carry a badge - <c>chat</c> and <c>chatFull</c> have no
/// <c>bot_verification</c> field, and the id of a legacy group would otherwise land in the channel
/// namespace.
/// </para>
/// </summary>
public class BotVerificationTests
{
    private const long OwnerUserId = 2_000_001;
    private const long TargetUserId = 2_000_002;
    private const long VerifierBotId = 2_000_010;
    private const long OtherVerifierBotId = 2_000_011;
    private const long ChannelId = 800_000_000_001;
    private const long IconDocumentId = 5_350_513_349_223_189_212;
    private const long OtherIconDocumentId = 5_350_513_349_223_189_213;

    [RequiresMongoDbFact]
    public async Task A_bot_without_verifier_settings_cannot_verify_anybody()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var fixture = CreateFixture(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(fixture.Handler, OwnerUserId, Request(VerifierBotId, InputPeerUser(), enabled: true)));

        exception.RpcError.Message.ShouldBe("BOT_VERIFIER_FORBIDDEN");
        exception.RpcError.ErrorCode.ShouldBe(403);
        // The rejection must happen before anything is written, otherwise an unauthorised bot still
        // gets a badge with an empty organisation on it.
        (await BadgesAsync(mongo.Database)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Verifier_settings_without_an_icon_are_not_a_licence_to_verify()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId, icon: 0);
        var fixture = CreateFixture(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(fixture.Handler, OwnerUserId, Request(VerifierBotId, InputPeerUser(), enabled: true)));

        exception.RpcError.Message.ShouldBe("BOT_VERIFIER_FORBIDDEN");
        (await BadgesAsync(mongo.Database)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_legacy_group_is_PEER_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId);
        var fixture = CreateFixture(mongo.Database);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(fixture.Handler, OwnerUserId,
                Request(VerifierBotId, new TInputPeerChat { ChatId = ChannelId }, enabled: true)));

        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
        // Storing the chat id would have made the badge show up on the channel with the same id.
        (await BadgesAsync(mongo.Database)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Without_a_description_the_organisation_name_is_used()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId);
        var fixture = CreateFixture(mongo.Database);

        var result = await InvokeAsync(fixture.Handler, OwnerUserId,
            Request(VerifierBotId, InputPeerUser(), enabled: true));

        result.ShouldBeOfType<TBoolTrue>();
        var badge = (await BadgesAsync(mongo.Database)).ShouldHaveSingleItem();
        badge.Description.ShouldBe("Was verified by organization \"Acme Inc.\"");
        badge.Icon.ShouldBe(IconDocumentId);
        badge.UserId.ShouldBe(TargetUserId);
    }

    [RequiresMongoDbFact]
    public async Task A_bot_that_may_not_modify_the_description_does_not_get_to_send_one()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId, customDescription: "Official partner",
            canModifyCustomDescription: false);
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync(fixture.Handler, OwnerUserId,
            Request(VerifierBotId, InputPeerUser(), enabled: true, customDescription: "Anything I like"));

        var badge = (await BadgesAsync(mongo.Database)).ShouldHaveSingleItem();
        badge.Description.ShouldBe("Official partner");
    }

    [RequiresMongoDbFact]
    public async Task A_bot_that_may_modify_the_description_gets_its_own_text()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId, customDescription: "Official partner",
            canModifyCustomDescription: true);
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync(fixture.Handler, OwnerUserId,
            Request(VerifierBotId, InputPeerUser(), enabled: true, customDescription: "Verified reseller"));

        var badge = (await BadgesAsync(mongo.Database)).ShouldHaveSingleItem();
        badge.Description.ShouldBe("Verified reseller");
    }

    [RequiresMongoDbFact]
    public async Task A_description_over_the_config_limit_is_rejected()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId, canModifyCustomDescription: true);
        var fixture = CreateFixture(mongo.Database);

        // bot_verification_description_length_limit is 70 UTF-8 bytes; these are two bytes each.
        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(fixture.Handler, OwnerUserId,
                Request(VerifierBotId, InputPeerUser(), enabled: true, customDescription: new string('я', 36))));

        exception.RpcError.Message.ShouldBe("DESCRIPTION_TOO_LONG");
        (await BadgesAsync(mongo.Database)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task One_verifier_cannot_revoke_the_badge_of_another()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId);
        await SeedVerifierSettingsAsync(mongo.Database, OtherVerifierBotId, icon: OtherIconDocumentId,
            company: "Other Inc.");
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync(fixture.Handler, OwnerUserId, Request(VerifierBotId, InputPeerUser(), enabled: true));

        var result = await InvokeAsync(fixture.Handler, OwnerUserId,
            Request(OtherVerifierBotId, InputPeerUser(), enabled: false));

        // The method is idempotent, so removing a badge that is not yours still answers true - but it
        // must not actually remove it.
        result.ShouldBeOfType<TBoolTrue>();
        (await BadgesAsync(mongo.Database)).ShouldHaveSingleItem().BotId.ShouldBe(VerifierBotId);
    }

    [RequiresMongoDbFact]
    public async Task The_issuing_verifier_can_revoke_its_own_badge()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId);
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync(fixture.Handler, OwnerUserId, Request(VerifierBotId, InputPeerUser(), enabled: true));
        var result = await InvokeAsync(fixture.Handler, OwnerUserId,
            Request(VerifierBotId, InputPeerUser(), enabled: false));

        result.ShouldBeOfType<TBoolTrue>();
        (await BadgesAsync(mongo.Database)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Verifying_a_user_pushes_an_updateUser_so_the_badge_shows_up_without_a_restart()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId);
        var fixture = CreateFixture(mongo.Database);

        await InvokeAsync(fixture.Handler, OwnerUserId, Request(VerifierBotId, InputPeerUser(), enabled: true));

        fixture.MessageSender.Verify(p => p.PushMessageToPeerAsync(
            It.Is<Peer>(peer => peer.PeerType == PeerType.User && peer.PeerId == TargetUserId),
            It.IsAny<TUpdates>(),
            It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(),
            It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<long>(), It.IsAny<MyTelegram.Core.PushData?>(),
            It.IsAny<List<long>?>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task Verifying_a_channel_notifies_the_channel_instead()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedVerifierSettingsAsync(mongo.Database, VerifierBotId);
        var fixture = CreateFixture(mongo.Database);

        var result = await InvokeAsync(fixture.Handler, OwnerUserId,
            Request(VerifierBotId, new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 4242 },
                enabled: true));

        result.ShouldBeOfType<TBoolTrue>();
        var badge = (await BadgesAsync(mongo.Database)).ShouldHaveSingleItem();
        badge.ChannelId.ShouldBe(ChannelId);
        badge.UserId.ShouldBe(0);
        fixture.ChannelNotifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), ChannelId),
            Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task Badges_of_a_whole_user_list_are_read_in_one_query()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo.Database);

        await store.SetAsync(new BotVerificationDocument
        {
            Id = $"verification-user-{TargetUserId}",
            BotId = VerifierBotId,
            Icon = IconDocumentId,
            Company = "Acme Inc.",
            Description = "d",
            UserId = TargetUserId
        });
        await store.SetAsync(new BotVerificationDocument
        {
            Id = $"verification-channel-{ChannelId}",
            BotId = VerifierBotId,
            Icon = IconDocumentId,
            Company = "Acme Inc.",
            Description = "d",
            ChannelId = ChannelId
        });

        var users = await store.GetForUsersAsync([TargetUserId, OwnerUserId]);

        users.Count.ShouldBe(1);
        users[TargetUserId].Icon.ShouldBe(IconDocumentId);
        // A channel badge carries UserId = 0, so it must not be picked up as the badge of user 0.
        users.ShouldNotContainKey(0);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private sealed record Fixture(
        object Handler,
        Mock<IObjectMessageSender> MessageSender,
        Mock<IChannelUpdateNotifier> ChannelNotifier);

    private static TInputPeerUser InputPeerUser() => new() { UserId = TargetUserId, AccessHash = 4242 };

    private static MyTelegram.Schema.Bots.RequestSetCustomVerification Request(long botId, IInputPeer peer,
        bool enabled, string? customDescription = null)
    {
        return new MyTelegram.Schema.Bots.RequestSetCustomVerification
        {
            Bot = new TInputUser { UserId = botId, AccessHash = 4242 },
            Peer = peer,
            Enabled = enabled,
            CustomDescription = customDescription
        };
    }

    private static Task SeedVerifierSettingsAsync(IMongoDatabase database, long botId,
        long icon = IconDocumentId,
        string company = "Acme Inc.",
        string? customDescription = null,
        bool canModifyCustomDescription = false)
    {
        return database.GetCollection<BotVerifierSettingsDocument>(BotVerificationStore.VerifierSettingsCollectionName)
            .InsertOneAsync(new BotVerifierSettingsDocument
            {
                BotId = botId,
                Icon = icon,
                Company = company,
                CustomDescription = customDescription,
                CanModifyCustomDescription = canModifyCustomDescription
            });
    }

    private static async Task<List<BotVerificationDocument>> BadgesAsync(IMongoDatabase database)
    {
        return await database.GetCollection<BotVerificationDocument>(BotVerificationStore.CollectionName)
            .Find(Builders<BotVerificationDocument>.Filter.Empty)
            .ToListAsync();
    }

    private static BotVerificationStore CreateStore(IMongoDatabase database)
    {
        return new BotVerificationStore(database,
            new BotVerificationCache(database, NullLogger<BotVerificationCache>.Instance));
    }

    private static Fixture CreateFixture(IMongoDatabase database)
    {
        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetAsync(It.IsAny<long?>()))
            .ReturnsAsync((long? userId) => UserReadModel(userId ?? 0));

        var channelReadModel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channelReadModel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(channelReadModel.Object);

        // Every bot in these tests belongs to OwnerUserId; ownership itself is covered by the
        // eventflow-userreadmodel lookup inside BotOwnershipChecker.
        database.GetCollection<BsonDocument>("bot-owners").InsertMany([
            new BsonDocument { { "BotId", VerifierBotId }, { "OwnerId", OwnerUserId } },
            new BsonDocument { { "BotId", OtherVerifierBotId }, { "OwnerId", OwnerUserId } }
        ]);

        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);
        accessHashHelper
            .Setup(p => p.CheckAccessHashAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<IInputPeer>()))
            .Returns(Task.CompletedTask);
        accessHashHelper
            .Setup(p => p.CheckAccessHashAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<IInputUser>()))
            .Returns(Task.CompletedTask);

        var messageSender = new Mock<IObjectMessageSender>(MockBehavior.Loose);
        var channelNotifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);

        var handlerType = typeof(BotVerificationStore).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Bots.SetCustomVerificationHandler", throwOnError: true)!;

        var handler = Activator.CreateInstance(handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                userAppService.Object,
                channelAppService.Object,
                CreateStore(database),
                new BotOwnershipChecker(database),
                accessHashHelper.Object,
                new Mock<IFromMessagePeerResolver>(MockBehavior.Loose).Object,
                channelNotifier.Object,
                messageSender.Object
            ],
            culture: null)!;

        return new Fixture(handler, messageSender, channelNotifier);
    }

    private static IUserReadModel UserReadModel(long userId)
    {
        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(userId);
        user.SetupGet(p => p.Bot).Returns(userId is VerifierBotId or OtherVerifierBotId);
        user.SetupGet(p => p.IsDeleted).Returns(false);

        return user.Object;
    }

    private static async Task<object?> InvokeAsync(object handler, long callerUserId, IObject request)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(callerUserId);

        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, [input.Object, request])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;

        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
