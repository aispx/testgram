using System.Reflection;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.WallPapers;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the per-chat wallpaper — <c>userFull.wallpaper</c> in the
/// <a href="https://corefork.telegram.org/api/peers#full-info-database">full info database</a>, kept
/// fresh with <c>updatePeerWallpaper</c>.
///
/// <para>
/// <c>messages.setChatWallPaper</c> stored the wallpaper id and nothing ever read it back, so the
/// wallpaper survived in the database but no client saw it and no update announced it. The
/// <c>for_both</c> flag likewise did nothing for the other side of the chat.
/// See https://corefork.telegram.org/api/wallpapers#installing-wallpapers-in-a-specific-chat-or-channel
/// </para>
/// </summary>
public class ChatWallPaperTests
{
    private const long CallerUserId = 2_000_001;
    private const long PeerUserId = 2_000_002;
    private const long AuthKeyId = 777;
    private const long WallPaperId = 5555;
    private const long ChannelId = 800_000_000_001;

    [RequiresMongoDbFact]
    public async Task A_stored_wallpaper_is_read_back_with_its_per_chat_settings()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var service = new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database));

        await service.SetChatWallPaperAsync(CallerUserId, PeerPeer, WallPaperId,
            new TWallPaperSettings { Blur = true, Intensity = 42 }, overridden: false);

        var (wallPaper, overridden) = await service.GetChatWallPaperAsync(CallerUserId, PeerPeer);

        var settings = wallPaper.ShouldBeOfType<TWallPaperNoFile>().Settings.ShouldBeOfType<TWallPaperSettings>();
        settings.Blur.ShouldBeTrue();
        settings.Intensity.ShouldBe(42);
        overridden.ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task A_wallpaper_the_other_side_installed_for_both_is_reported_as_overridden()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var service = new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database));

        await service.SetChatWallPaperAsync(PeerUserId, CallerPeer, WallPaperId, null, overridden: true);

        var (_, overridden) = await service.GetChatWallPaperAsync(PeerUserId, CallerPeer);

        overridden.ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task Removing_the_wallpaper_leaves_nothing_behind()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var service = new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database));
        await service.SetChatWallPaperAsync(CallerUserId, PeerPeer, WallPaperId, null, overridden: false);

        await service.SetChatWallPaperAsync(CallerUserId, PeerPeer, null, null, overridden: false);

        var (wallPaper, _) = await service.GetChatWallPaperAsync(CallerUserId, PeerPeer);
        wallPaper.ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task A_wallpaper_stored_by_the_previous_dialog_field_is_still_served()
    {
        // Chats that already had a wallpaper keep it: it used to live in the dialog read model.
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        await mongo.Database.GetCollection<BsonDocument>("eventflow-dialogreadmodel").InsertOneAsync(new BsonDocument
        {
            { "_id", DialogId.Create(CallerUserId, PeerType.User, PeerUserId).Value },
            { "WallpaperId", WallPaperId }
        });

        var (wallPaper, overridden) = await new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database))
            .GetChatWallPaperAsync(CallerUserId, PeerPeer);

        wallPaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);
        overridden.ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task An_unknown_slug_is_WALLPAPER_NOT_FOUND()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            service.ResolveWallPaperIdAsync(new TInputWallPaperSlug { Slug = "nope" }));

        exception.RpcError.Message.ShouldBe("WALLPAPER_NOT_FOUND");
    }

    [RequiresMongoDbFact]
    public async Task Setting_a_wallpaper_tells_the_callers_other_sessions()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var sender = new RecordingWallPaperSender();
        var handler = CreateHandler(mongo.Database, sender);

        await InvokeAsync(handler, ForBoth: false);

        var update = sender.UpdateFor(CallerUserId);
        update.Peer.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(PeerUserId);
        update.Wallpaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);
        update.WallpaperOverridden.ShouldBeFalse();
        sender.ExcludeAuthKeyIdFor(CallerUserId).ShouldBe(AuthKeyId);
        sender.WasPushedTo(PeerUserId).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task for_both_installs_the_wallpaper_on_the_other_side_as_overridden()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var sender = new RecordingWallPaperSender();
        var handler = CreateHandler(mongo.Database, sender);

        await InvokeAsync(handler, ForBoth: true);

        var update = sender.UpdateFor(PeerUserId);
        update.Peer.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(CallerUserId);
        update.WallpaperOverridden.ShouldBeTrue();

        var (wallPaper, overridden) = await new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database))
            .GetChatWallPaperAsync(PeerUserId, CallerPeer);
        wallPaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);
        overridden.ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task A_channel_wallpaper_belongs_to_the_channel_and_reaches_its_members()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var sender = new RecordingWallPaperSender();
        var handler = CreateHandler(mongo.Database, sender);

        await InvokeAsync(handler, ForBoth: false,
            peer: new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 });

        sender.UpdateFor(ChannelId).Peer.ShouldBeOfType<TPeerChannel>().ChannelId.ShouldBe(ChannelId);

        // Stored under the channel itself, so every member reads the same wallpaper.
        var (wallPaper, _) = await new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database))
            .GetChatWallPaperAsync(ChannelId, new Peer(PeerType.Channel, ChannelId));
        wallPaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);
    }

    [RequiresMongoDbFact]
    public async Task A_channel_wallpaper_set_by_a_non_admin_is_CHAT_ADMIN_REQUIRED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender(), callerIsChannelAdmin: false);

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(handler, ForBoth: false,
            peer: new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 }));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
    }

    /// <summary>
    /// The other user accepting the invitation sends the id of the service message and <b>no</b> wallpaper.
    /// This used to resolve to "no wallpaper" and remove the wallpaper it was asked to apply.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Applying_the_wallpaper_named_by_a_service_message_keeps_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var sent = new List<SendMessageInput>();
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender(),
            serviceMessage: ServiceMessageWith(new TWallPaperNoFile { Id = WallPaperId }), sentMessages: sent);

        await InvokeAsync(handler, ForBoth: false, messageId: 42);

        var (wallPaper, _) = await new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database))
            .GetChatWallPaperAsync(CallerUserId, PeerPeer);
        wallPaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);

        // same is what makes clients draw an acknowledgment instead of a second invitation.
        var action = sent.Single().MessageAction.ShouldBeOfType<TMessageActionSetChatWallPaper>();
        action.Same.ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task A_service_message_naming_something_else_is_WALLPAPER_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender());

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, ForBoth: false, messageId: 42));

        exception.RpcError.Message.ShouldBe("WALLPAPER_INVALID");
    }

    /// <summary>
    /// A manual change announces itself to the chat; <c>revert</c> is a one-sided undo and announces
    /// nothing. The rule used to be exactly inverted.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_manual_change_sends_a_service_message_and_a_revert_does_not()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var sent = new List<SendMessageInput>();
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender(), sentMessages: sent);

        await InvokeAsync(handler, ForBoth: false);
        sent.Count.ShouldBe(1);
        sent.Single().MessageAction.ShouldBeOfType<TMessageActionSetChatWallPaper>().Same.ShouldBeFalse();

        await InvokeAsync(handler, ForBoth: false, revert: true);
        sent.Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task A_revert_puts_the_previous_wallpaper_back()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        await mongo.Database.GetCollection<BsonDocument>("wallpapers").InsertOneAsync(new BsonDocument
        {
            { "WallpaperId", 6666L }, { "AccessHash", 1L }, { "Slug", "dawn" }, { "DocumentId", 0L }
        });
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender());

        await InvokeAsync(handler, ForBoth: false);
        await InvokeAsync(handler, ForBoth: false, wallpaper: new TInputWallPaper { Id = 6666, AccessHash = 1 });
        await InvokeAsync(handler, ForBoth: false, revert: true);

        var (wallPaper, _) = await new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database))
            .GetChatWallPaperAsync(CallerUserId, PeerPeer);
        wallPaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);
    }

    /// <summary>
    /// A channel fill wallpaper is <c>inputWallPaperNoFile{id = 0}</c> plus <c>settings.emoticon</c>, which
    /// names no catalogue row. Reading the zero id as "remove" is why setting a channel wallpaper from
    /// Android cleared it instead.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_channel_fill_wallpaper_is_stored_with_its_emoticon()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sender = new RecordingWallPaperSender();
        var handler = CreateHandler(mongo.Database, sender);

        await InvokeAsync(handler, ForBoth: false,
            peer: new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 },
            wallpaper: new TInputWallPaperNoFile { Id = 0 },
            settings: new TWallPaperSettings { Emoticon = "🌅", BackgroundColor = 123 });

        var (wallPaper, _) = await new ChatWallPaperService(mongo.Database, CreateCatalog(mongo.Database))
            .GetChatWallPaperAsync(ChannelId, new Peer(PeerType.Channel, ChannelId));

        var settings = wallPaper.ShouldBeOfType<TWallPaperNoFile>().Settings.ShouldBeOfType<TWallPaperSettings>();
        settings.Emoticon.ShouldBe("🌅");
        sender.UpdateFor(ChannelId).Wallpaper.ShouldNotBeNull();
    }

    [RequiresMongoDbFact]
    public async Task A_channel_wallpaper_below_the_boost_level_is_BOOSTS_REQUIRED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender(), boostLevel: 8);

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(handler, ForBoth: false,
            peer: new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 }));

        exception.RpcError.Message.ShouldBe("BOOSTS_REQUIRED");
    }

    /// <summary>
    /// A <c>getChatThemes</c> fill wallpaper needs <c>channel_wallpaper_level_min</c> (9), a custom one
    /// needs <c>channel_custom_wallpaper_level_min</c> (10) — so level 9 takes the first and refuses the
    /// second.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task At_level_nine_a_chat_theme_wallpaper_is_allowed_and_a_custom_one_is_not()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var handler = CreateHandler(mongo.Database, new RecordingWallPaperSender(), boostLevel: 9);
        var channel = new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 1 };

        await InvokeAsync(handler, ForBoth: false, peer: channel,
            wallpaper: new TInputWallPaperNoFile { Id = 0 },
            settings: new TWallPaperSettings { Emoticon = "🌅" });

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, ForBoth: false, peer: channel));
        exception.RpcError.Message.ShouldBe("BOOSTS_REQUIRED");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static Peer PeerPeer => new(PeerType.User, PeerUserId);
    private static Peer CallerPeer => new(PeerType.User, CallerUserId);

    private static WallPaperCatalog CreateCatalog(IMongoDatabase database)
    {
        return new WallPaperCatalog(database, TestFileReferences.Helper, NullLogger<WallPaperCatalog>.Instance);
    }

    private static Task SeedWallPaperAsync(IMongoDatabase database)
    {
        return database.GetCollection<BsonDocument>("wallpapers").InsertOneAsync(new BsonDocument
        {
            { "WallpaperId", WallPaperId },
            { "AccessHash", 99L },
            { "Slug", "sunset" },
            { "DocumentId", 0L }
        });
    }

    /// <summary>A stored service message whose action carries the given wallpaper.</summary>
    private static IMessageReadModel ServiceMessageWith(IWallPaper wallPaper)
    {
        var message = new Mock<IMessageReadModel>(MockBehavior.Loose);
        message.SetupGet(p => p.MessageAction)
            .Returns(new TMessageActionSetChatWallPaper { Wallpaper = wallPaper });

        return message.Object;
    }

    private static object CreateHandler(IMongoDatabase database, IObjectMessageSender sender,
        bool callerIsChannelAdmin = true, int boostLevel = 10, IMessageReadModel? serviceMessage = null,
        List<SendMessageInput>? sentMessages = null)
    {
        var messageAppService = new Mock<IMessageAppService>(MockBehavior.Loose);
        messageAppService.Setup(p => p.SendMessageAsync(It.IsAny<List<SendMessageInput>>()))
            .Returns((List<SendMessageInput> inputs) =>
            {
                sentMessages?.AddRange(inputs);

                return Task.CompletedTask;
            });

        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);

        var adminRightsChecker = new Mock<IChannelAdminRightsChecker>(MockBehavior.Loose);
        adminRightsChecker
            .Setup(p => p.CheckAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<Func<ChatAdminRights, bool>>(), It.IsAny<RpcError?>()))
            .Returns(() => callerIsChannelAdmin
                ? Task.CompletedTask
                : throw new RpcException(RpcErrors.RpcErrors400.ChatAdminRequired));

        var boostLevelCalculator = new Mock<IBoostLevelCalculator>(MockBehavior.Loose);
        boostLevelCalculator.Setup(p => p.GetLevelAsync(It.IsAny<long>())).ReturnsAsync(boostLevel);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetMessagesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(serviceMessage == null
                ? new List<IMessageReadModel>()
                : [serviceMessage]);

        var handlerType = typeof(ChatWallPaperService).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Messages.SetChatWallPaperHandler", throwOnError: true)!;

        return Activator.CreateInstance(handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                messageAppService.Object, new PeerHelper(),
                new ChatWallPaperService(database, CreateCatalog(database)), sender,
                accessHashHelper.Object, adminRightsChecker.Object, boostLevelCalculator.Object,
                queryProcessor.Object
            ],
            culture: null)!;
    }

    private static async Task InvokeAsync(object handler, bool ForBoth, IInputPeer? peer = null,
        IInputWallPaper? wallpaper = null, IWallPaperSettings? settings = null, bool revert = false,
        int? messageId = null)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(CallerUserId);
        input.SetupGet(p => p.AuthKeyId).Returns(AuthKeyId);
        input.SetupGet(p => p.PermAuthKeyId).Returns(AuthKeyId);

        var request = new MyTelegram.Schema.Messages.RequestSetChatWallPaper
        {
            Peer = peer ?? new TInputPeerUser { UserId = PeerUserId, AccessHash = 0 },
            Wallpaper = wallpaper ?? (messageId.HasValue || revert
                ? null
                : new TInputWallPaper { Id = WallPaperId, AccessHash = 99 }),
            Settings = settings,
            Revert = revert,
            Id = messageId,
            ForBoth = ForBoth
        };

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
    }

    private sealed class RecordingWallPaperSender : IObjectMessageSender
    {
        private readonly List<(long UserId, TUpdates Updates, long? ExcludeAuthKeyId)> _pushes = [];

        public TUpdatePeerWallpaper UpdateFor(long userId) =>
            (TUpdatePeerWallpaper)_pushes.Single(p => p.UserId == userId).Updates.Updates.Single();

        public bool WasPushedTo(long userId) => _pushes.Any(p => p.UserId == userId);

        public long? ExcludeAuthKeyIdFor(long userId) => _pushes.Single(p => p.UserId == userId).ExcludeAuthKeyId;

        public Task PushMessageToPeerAsync<TData>(Peer peer, TData data, long? excludeAuthKeyId = null,
            long? excludeUserId = null, long? onlySendToUserId = null, long? onlySendToThisAuthKeyId = null,
            int pts = 0, int? qts = null, long globalSeqNo = 0, PushData? pushData = null,
            List<long>? excludeUserIds = null) where TData : IObject
        {
            _pushes.Add((peer.PeerId, (TUpdates)(object)data!, excludeAuthKeyId));

            return Task.CompletedTask;
        }

        public Task PushSessionMessageToAuthKeyIdAsync<TData>(long authKeyId, TData data, int pts = 0, int? qts = null,
            long globalSeqNo = 0) where TData : IObject => throw new NotSupportedException();

        public Task SendFileDataToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject =>
            throw new NotSupportedException();

        public Task SendMessageToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject =>
            throw new NotSupportedException();

        public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, int pts = 0)
            where TData : IObject => throw new NotSupportedException();

        public Task SendRpcMessageToClientAsync<TData>(string connectionId, long tempAuthKeyId, long sessionId,
            long reqMsgId, TData data, int pts = 0, long permAuthKeyId = 0) where TData : IObject =>
            throw new NotSupportedException();

        public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, long authKeyId,
            long permAuthKeyId, long userId, int pts = 0) where TData : IObject => throw new NotSupportedException();
    }
}
