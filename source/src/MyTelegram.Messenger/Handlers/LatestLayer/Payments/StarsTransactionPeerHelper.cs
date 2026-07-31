using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Resolves the counterparty peers referenced by a stars/TON transaction history into the
/// <c>chats</c>/<c>users</c> vectors of <c>payments.starsStatus</c>. Without these vectors official
/// clients cannot render transaction rows (they fall back to "Deleted Account"/blank entries).
/// </summary>
internal static class StarsTransactionPeerHelper
{
    public static async Task<(TVector<IChat> Chats, TVector<IUser> Users)> ResolveAsync(
        IRequestInput input,
        IEnumerable<IStarsTransaction> history,
        IUserConverterService userConverterService,
        IChatConverterService chatConverterService)
    {
        var userIds = new HashSet<long>();
        var channelIds = new HashSet<long>();

        foreach (var transaction in history)
        {
            if (transaction is not TStarsTransaction t)
            {
                continue;
            }

            switch (t.Peer)
            {
                case TStarsTransactionPeer { Peer: TPeerUser peerUser }:
                    userIds.Add(peerUser.UserId);
                    break;
                case TStarsTransactionPeer { Peer: TPeerChannel peerChannel }:
                    channelIds.Add(peerChannel.ChannelId);
                    break;
            }

            switch (t.StarrefPeer)
            {
                case TPeerUser starrefUser:
                    userIds.Add(starrefUser.UserId);
                    break;
                case TPeerChannel starrefChannel:
                    channelIds.Add(starrefChannel.ChannelId);
                    break;
            }
        }

        var chats = channelIds.Count == 0
            ? new List<IChat>()
            : await chatConverterService.GetChannelListAsync(input, channelIds.ToList(), layer: input.Layer);
        var users = userIds.Count == 0
            ? new List<ILayeredUser>()
            : await userConverterService.GetUserListAsync(input, userIds.ToList(), layer: input.Layer);

        return (new TVector<IChat>(chats), new TVector<IUser>(users.Cast<IUser>()));
    }
}
