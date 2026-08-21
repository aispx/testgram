using System.Reflection;
using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the channel half of the username methods — <c>channels.toggleUsername</c>,
/// <c>channels.reorderUsernames</c> and <c>channels.deactivateAllUsernames</c>.
///
/// <para>
/// All three answer with a plain <c>Bool</c>, so members only learn about the change from an
/// <c>updateChannel</c>; without it their
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a> keeps
/// the old username list. They also used to take the channel id straight from the request and edit
/// the read model without asking who the caller was, which let anybody deactivate the usernames of
/// any channel on the server.
/// </para>
/// </summary>
public class ChannelUsernameHandlerTests
{
    private const long OwnerUserId = 2_000_001;
    private const long StrangerUserId = 2_000_002;
    private const long ChannelId = 800_000_000_001;

    [RequiresMongoDbFact]
    public async Task toggleUsername_deactivates_a_fragment_username_and_notifies()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "ToggleUsernameHandler", notifier, OwnerUserId);

        var result = await InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
        {
            Channel = InputChannel(),
            Username = "collectible",
            Active = false
        });

        result.ShouldBeOfType<TBoolTrue>();
        (await ActiveUsernamesAsync(mongo.Database)).ShouldBe(["basic"]);
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), ChannelId), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task toggleUsername_by_someone_who_is_not_the_owner_is_CHAT_ADMIN_REQUIRED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "ToggleUsernameHandler", notifier, OwnerUserId);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, StrangerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
            {
                Channel = InputChannel(),
                Username = "collectible",
                Active = false
            }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
        // The rejection has to happen before the read model is touched.
        (await ActiveUsernamesAsync(mongo.Database)).ShouldBe(["basic", "collectible"]);
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), It.IsAny<long>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task toggleUsername_with_a_forged_access_hash_is_rejected()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "ToggleUsernameHandler", notifier, OwnerUserId,
            validAccessHash: 4242);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestToggleUsername
            {
                Channel = new TInputChannel { ChannelId = ChannelId, AccessHash = 1 },
                Username = "collectible",
                Active = false
            }));

        exception.RpcError.Message.ShouldBe("CHANNEL_INVALID");
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), It.IsAny<long>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task reorderUsernames_reorders_and_notifies()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "ReorderUsernamesHandler", notifier, OwnerUserId);

        var result = await InvokeAsync(handler, OwnerUserId, new MyTelegram.Schema.Channels.RequestReorderUsernames
        {
            Channel = InputChannel(),
            Order = new TVector<string>("collectible", "basic")
        });

        result.ShouldBeOfType<TBoolTrue>();
        (await ActiveUsernamesAsync(mongo.Database)).ShouldBe(["collectible", "basic"]);
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), ChannelId), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task reorderUsernames_by_someone_who_is_not_the_owner_is_CHAT_ADMIN_REQUIRED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "ReorderUsernamesHandler", notifier, OwnerUserId);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, StrangerUserId, new MyTelegram.Schema.Channels.RequestReorderUsernames
            {
                Channel = InputChannel(),
                Order = new TVector<string>("collectible", "basic")
            }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
        (await ActiveUsernamesAsync(mongo.Database)).ShouldBe(["basic", "collectible"]);
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), It.IsAny<long>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task deactivateAllUsernames_keeps_the_basic_username_and_notifies()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "DeactivateAllUsernamesHandler", notifier, OwnerUserId);

        var result = await InvokeAsync(handler, OwnerUserId,
            new MyTelegram.Schema.Channels.RequestDeactivateAllUsernames { Channel = InputChannel() });

        result.ShouldBeOfType<TBoolTrue>();
        (await ActiveUsernamesAsync(mongo.Database)).ShouldBe(["basic"]);
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), ChannelId), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task deactivateAllUsernames_by_someone_who_is_not_the_owner_is_CHAT_ADMIN_REQUIRED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedChannelAsync(mongo.Database);
        var notifier = new Mock<IChannelUpdateNotifier>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, "DeactivateAllUsernamesHandler", notifier, OwnerUserId);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, StrangerUserId,
                new MyTelegram.Schema.Channels.RequestDeactivateAllUsernames { Channel = InputChannel() }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
        (await ActiveUsernamesAsync(mongo.Database)).ShouldBe(["basic", "collectible"]);
        notifier.Verify(p => p.NotifyChannelChangedAsync(It.IsAny<IRequestInput>(), It.IsAny<long>()), Times.Never);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static TInputChannel InputChannel() => new() { ChannelId = ChannelId, AccessHash = 4242 };

    private static Task SeedChannelAsync(IMongoDatabase database)
    {
        var channel = new BsonDocument
        {
            { "ChannelId", ChannelId },
            { "CreatorId", OwnerUserId },
            {
                "Usernames", new BsonArray
                {
                    new BsonDocument { { "Username", "basic" }, { "Editable", true }, { "Active", true } },
                    new BsonDocument { { "Username", "collectible" }, { "Editable", false }, { "Active", true } }
                }
            }
        };

        return database.GetCollection<BsonDocument>("eventflow-channelreadmodel").InsertOneAsync(channel);
    }

    private static async Task<List<string>> ActiveUsernamesAsync(IMongoDatabase database)
    {
        var channel = await database.GetCollection<BsonDocument>("eventflow-channelreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("ChannelId", ChannelId))
            .FirstAsync();

        return channel["Usernames"].AsBsonArray
            .Select(p => p.AsBsonDocument)
            .Where(p => p["Active"].AsBoolean)
            .Select(p => p["Username"].AsString)
            .ToList();
    }

    private static object CreateHandler(IMongoDatabase database,
        string handlerName,
        Mock<IChannelUpdateNotifier> notifier,
        long creatorUserId,
        long validAccessHash = 4242)
    {
        var channelReadModel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channelReadModel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        channelReadModel.SetupGet(p => p.CreatorId).Returns(creatorUserId);
        channelReadModel.SetupGet(p => p.AdminList).Returns([]);

        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(channelReadModel.Object);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long>())).ReturnsAsync(channelReadModel.Object);

        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);
        accessHashHelper
            .Setup(p => p.CheckAccessHashAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<IInputChannel>()))
            .Returns((IRequestWithAccessHashKeyId _, IInputChannel channel) =>
                channel is TInputChannel { } inputChannel && inputChannel.AccessHash == validAccessHash
                    ? Task.CompletedTask
                    : throw new RpcException(RpcErrors.RpcErrors400.ChannelInvalid));

        var checker = new ChannelAdminRightsChecker(new Mock<IQueryProcessor>(MockBehavior.Loose).Object,
            channelAppService.Object);

        var handlerType = typeof(ChannelAdminRightsChecker).Assembly.GetType(
            $"MyTelegram.Messenger.Handlers.LatestLayer.Channels.{handlerName}", throwOnError: true)!;

        return Activator.CreateInstance(handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [database, accessHashHelper.Object, checker, notifier.Object],
            culture: null)!;
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
