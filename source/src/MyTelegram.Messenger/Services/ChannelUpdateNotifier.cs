using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Pushes <c>updateChannel</c> after a channel entry changed through a handler that writes to the
/// read model directly, such as <c>channels.toggleUsername</c>, <c>channels.reorderUsernames</c> and
/// <c>channels.deactivateAllUsernames</c>.
/// <para>
/// Those methods answer with a plain <c>Bool</c>, so the only way the change reaches anybody other
/// than the calling session is an update. Without it every member keeps the previous username list
/// in their <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info
/// database</a> until something else forces a refetch.
/// </para>
/// </summary>
public interface IChannelUpdateNotifier
{
    /// <summary>
    /// Delivers <c>updateChannel</c> to the caller's other sessions and to the channel members. The
    /// calling session is skipped: it already learns the outcome from the rpc result.
    /// </summary>
    Task NotifyChannelChangedAsync(IRequestInput input, long channelId);
}

public sealed class ChannelUpdateNotifier(
    IChannelAppService channelAppService,
    IChatConverterService chatConverterService,
    IObjectMessageSender objectMessageSender)
    : IChannelUpdateNotifier, ITransientDependency
{
    public async Task NotifyChannelChangedAsync(IRequestInput input, long channelId)
    {
        // The username handlers write straight to the read model collection, so the cached copy must
        // be dropped before the channel is converted below.
        channelAppService.InvalidateCache(channelId);

        var date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Members get the bare update and refetch the channel themselves. The access_hash inside a
        // channel constructor is derived per recipient, so one broadcast copy would hand everybody
        // else a hash that is not theirs.
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateChannel { ChannelId = channelId }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = date
        };

        var channel = await chatConverterService.GetChannelAsync(input, channelId, false, false, input.Layer,
            throwIfNotFound: false);
        if (channel != null)
        {
            // The caller's own other sessions may take the channel as is: it was built for them.
            var selfUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate>(new TUpdateChannel { ChannelId = channelId }),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(channel),
                Date = date
            };

            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), selfUpdates,
                excludeAuthKeyId: input.AuthKeyId);
        }

        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.Channel, channelId), updates,
            excludeUserId: input.UserId);
    }
}
