using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Pushes <c>updateUserStatus</c> after a user goes online or offline.
/// <para>
/// <c>user.status</c> is part of the
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a> and is
/// meant to be kept fresh reactively; without this update a contact list shows whoever was online
/// when it was last fetched.
/// </para>
/// </summary>
public interface IUserStatusUpdateNotifier
{
    /// <summary>
    /// Delivers the current status to the user's other sessions and to everyone who has them as a
    /// contact, masking the exact timestamp for viewers disallowed by <c>privacyKeyStatusTimestamp</c>.
    /// </summary>
    Task NotifyStatusChangedAsync(IRequestInput input, long userId);
}

public sealed class UserStatusUpdateNotifier(
    IUserStatusCacheAppService userStatusCacheAppService,
    IPrivacyAppService privacyAppService,
    IQueryProcessor queryProcessor,
    IObjectMessageSender objectMessageSender)
    : IUserStatusUpdateNotifier, ITransientDependency
{
    public async Task NotifyStatusChangedAsync(IRequestInput input, long userId)
    {
        var status = userStatusCacheAppService.GetUserStatus(userId);

        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), StatusUpdates(userId, status),
            excludeAuthKeyId: input.AuthKeyId);

        var viewerIds = await queryProcessor.ProcessAsync(
            new GetContactSelfUserIdListByTargetUserIdQuery(userId));

        var maskedStatus = PrivacyMaskingHelper.MaskStatusTimestamp(status);

        foreach (var viewerId in viewerIds.Where(p => p != userId).Distinct())
        {
            var visibleStatus = status;
            await privacyAppService.ApplyPrivacyAsync(viewerId, userId, _ => visibleStatus = maskedStatus,
                PrivacyType.StatusTimestamp);

            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, viewerId),
                StatusUpdates(userId, visibleStatus));
        }
    }

    private static TUpdates StatusUpdates(long userId, IUserStatus status)
    {
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateUserStatus { UserId = userId, Status = status }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }
}
