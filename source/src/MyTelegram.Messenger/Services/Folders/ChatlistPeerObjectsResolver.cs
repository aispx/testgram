namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// Loads the <c>chats</c> and <c>users</c> that travel with a shared-folder answer.
///
/// <para>Every <c>chatlists.*</c> constructor carries the peers it names, because the client has to draw
/// chats it may never have loaded — an answer without them shows a folder of blank rows.</para>
/// </summary>
public interface IChatlistPeerObjectsResolver
{
    Task<(TVector<IChat> Chats, TVector<IUser> Users)> ResolveAsync(IRequestInput input,
        IReadOnlyCollection<Peer> peers);
}

/// <inheritdoc />
public class ChatlistPeerObjectsResolver(
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService) : IChatlistPeerObjectsResolver, ITransientDependency
{
    public async Task<(TVector<IChat> Chats, TVector<IUser> Users)> ResolveAsync(IRequestInput input,
        IReadOnlyCollection<Peer> peers)
    {
        var chats = new TVector<IChat>();
        var users = new TVector<IUser>();

        var channelIds = peers.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId).Distinct().ToList();
        if (channelIds.Count > 0)
        {
            var channelMembers = await queryProcessor.ProcessAsync(
                new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
            chats.AddRange(await chatConverterService.GetChannelListAsync(input, channelIds, channelMembers,
                layer: input.Layer));
        }

        var userIds = peers.Where(p => p.PeerType is PeerType.User or PeerType.Self)
            .Select(p => p.PeerId)
            .Distinct()
            .ToList();
        if (userIds.Count > 0)
        {
            users.AddRange(await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer));
        }

        return (chats, users);
    }
}
