using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
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
        var service = new ChatWallPaperService(mongo.Database, TestFileReferences.Helper);

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
        var service = new ChatWallPaperService(mongo.Database, TestFileReferences.Helper);

        await service.SetChatWallPaperAsync(PeerUserId, CallerPeer, WallPaperId, null, overridden: true);

        var (_, overridden) = await service.GetChatWallPaperAsync(PeerUserId, CallerPeer);

        overridden.ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task Removing_the_wallpaper_leaves_nothing_behind()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database);
        var service = new ChatWallPaperService(mongo.Database, TestFileReferences.Helper);
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

        var (wallPaper, overridden) = await new ChatWallPaperService(mongo.Database, TestFileReferences.Helper)
            .GetChatWallPaperAsync(CallerUserId, PeerPeer);

        wallPaper!.ShouldBeOfType<TWallPaperNoFile>().Id.ShouldBe(WallPaperId);
        overridden.ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task An_unknown_slug_is_WALLPAPER_NOT_FOUND()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new ChatWallPaperService(mongo.Database, TestFileReferences.Helper);

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

        var (wallPaper, overridden) = await new ChatWallPaperService(mongo.Database, TestFileReferences.Helper)
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
        var (wallPaper, _) = await new ChatWallPaperService(mongo.Database, TestFileReferences.Helper)
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

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static Peer PeerPeer => new(PeerType.User, PeerUserId);
    private static Peer CallerPeer => new(PeerType.User, CallerUserId);

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

    private static object CreateHandler(IMongoDatabase database, IObjectMessageSender sender,
        bool callerIsChannelAdmin = true)
    {
        var messageAppService = new Mock<IMessageAppService>(MockBehavior.Loose);
        var ptsHelper = new Mock<IPtsHelper>(MockBehavior.Loose);
        var accessHashHelper = new Mock<IAccessHashHelper2>(MockBehavior.Loose);

        var adminRightsChecker = new Mock<IChannelAdminRightsChecker>(MockBehavior.Loose);
        adminRightsChecker
            .Setup(p => p.CheckAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<Func<ChatAdminRights, bool>>(), It.IsAny<RpcError?>()))
            .Returns(() => callerIsChannelAdmin
                ? Task.CompletedTask
                : throw new RpcException(RpcErrors.RpcErrors400.ChatAdminRequired));

        var handlerType = typeof(ChatWallPaperService).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Messages.SetChatWallPaperHandler", throwOnError: true)!;

        return Activator.CreateInstance(handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                messageAppService.Object, new PeerHelper(), new ChatWallPaperService(database, TestFileReferences.Helper), sender,
                accessHashHelper.Object, adminRightsChecker.Object, ptsHelper.Object
            ],
            culture: null)!;
    }

    private static async Task InvokeAsync(object handler, bool ForBoth, IInputPeer? peer = null)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(CallerUserId);
        input.SetupGet(p => p.AuthKeyId).Returns(AuthKeyId);

        var request = new MyTelegram.Schema.Messages.RequestSetChatWallPaper
        {
            Peer = peer ?? new TInputPeerUser { UserId = PeerUserId, AccessHash = 0 },
            Wallpaper = new TInputWallPaper { Id = WallPaperId, AccessHash = 99 },
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
