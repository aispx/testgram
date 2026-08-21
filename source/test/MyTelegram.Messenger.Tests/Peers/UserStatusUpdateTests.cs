using EventFlow.Queries;
using Moq;
using MyTelegram.Core;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: <c>updateUserStatus</c>, the reactive half of <c>user.status</c> in the
/// <a href="https://corefork.telegram.org/api/peers#handling-updates">peer info database</a>.
///
/// <para>
/// <c>account.updateStatus</c> used to only write the presence to a cache, so a contact list showed
/// whoever happened to be online when it was last fetched. The push is rate-limited by meaning
/// rather than by time: clients re-ping every minute, and only a change of the visible status
/// constructor is worth telling everybody about. The exact last-seen timestamp is masked for viewers
/// disallowed by <c>privacyKeyStatusTimestamp</c>, the same way <c>users.getUsers</c> masks it.
/// </para>
/// </summary>
public class UserStatusUpdateTests
{
    private const long UserId = 2_000_001;
    private const long ContactUserId = 2_000_002;
    private const long AuthKeyId = 777;

    [Fact]
    public async Task Contacts_are_told_about_the_new_status()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyStatusChangedAsync(Input(), UserId);

        var update = sender.UpdateFor(ContactUserId);
        update.UserId.ShouldBe(UserId);
        update.Status.ShouldBeOfType<TUserStatusOnline>();
    }

    [Fact]
    public async Task The_users_own_other_sessions_are_told_too()
    {
        var (notifier, sender) = CreateNotifier();

        await notifier.NotifyStatusChangedAsync(Input(), UserId);

        sender.UpdateFor(UserId).Status.ShouldBeOfType<TUserStatusOnline>();
        // The session that reported the presence already knows.
        sender.ExcludeAuthKeyIdFor(UserId).ShouldBe(AuthKeyId);
    }

    [Fact]
    public async Task A_viewer_disallowed_by_the_privacy_rule_only_gets_userStatusRecently()
    {
        var (notifier, sender) = CreateNotifier(contactMaySeeTimestamp: false);

        await notifier.NotifyStatusChangedAsync(Input(), UserId);

        sender.UpdateFor(ContactUserId).Status.ShouldBeOfType<TUserStatusRecently>();
        // The user's own sessions still see the exact status.
        sender.UpdateFor(UserId).Status.ShouldBeOfType<TUserStatusOnline>();
    }

    [RequiresMongoDbFact]
    public void Going_online_for_the_first_time_counts_as_a_change()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new UserStatusCacheAppService(new InMemoryRepository<UserStatus, long>(), mongo.Database);

        service.UpdateStatus(UserId, online: true).ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public void Re_pinging_while_already_online_is_not_a_change()
    {
        // Clients re-send account.updateStatus(offline=false) about every minute; fanning that out to
        // every contact each time would be pure noise.
        using var mongo = EmbeddedMongoServer.Start();
        var service = new UserStatusCacheAppService(new InMemoryRepository<UserStatus, long>(), mongo.Database);
        service.UpdateStatus(UserId, online: true);

        service.UpdateStatus(UserId, online: true).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public void Going_offline_is_a_change()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = new UserStatusCacheAppService(new InMemoryRepository<UserStatus, long>(), mongo.Database);
        service.UpdateStatus(UserId, online: true);

        service.UpdateStatus(UserId, online: false).ShouldBeTrue();
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static IRequestInput Input()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(UserId);
        input.SetupGet(p => p.AuthKeyId).Returns(AuthKeyId);

        return input.Object;
    }

    private static (UserStatusUpdateNotifier, RecordingStatusSender) CreateNotifier(
        bool contactMaySeeTimestamp = true)
    {
        var statusCache = new Mock<IUserStatusCacheAppService>(MockBehavior.Loose);
        statusCache.Setup(p => p.GetUserStatus(UserId))
            .Returns(new TUserStatusOnline { Expires = 1_700_000_000 });

        var privacyAppService = new Mock<IPrivacyAppService>(MockBehavior.Loose);
        privacyAppService
            .Setup(p => p.ApplyPrivacyAsync(It.IsAny<long>(), UserId, It.IsAny<Action<PrivacyValueType>>(),
                PrivacyType.StatusTimestamp))
            .Returns((long _, long _, Action<PrivacyValueType> onNotMatch, PrivacyType _) =>
            {
                if (!contactMaySeeTimestamp)
                {
                    onNotMatch(PrivacyValueType.DisallowAll);
                }

                return Task.CompletedTask;
            });

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetContactSelfUserIdListByTargetUserIdQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<long>)[ContactUserId]);

        var sender = new RecordingStatusSender();

        return (new UserStatusUpdateNotifier(statusCache.Object, privacyAppService.Object, queryProcessor.Object,
            sender), sender);
    }

    private sealed class RecordingStatusSender : IObjectMessageSender
    {
        private readonly List<(long UserId, TUpdates Updates, long? ExcludeAuthKeyId)> _pushes = [];

        public TUpdateUserStatus UpdateFor(long userId) =>
            (TUpdateUserStatus)_pushes.Single(p => p.UserId == userId).Updates.Updates.Single();

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
