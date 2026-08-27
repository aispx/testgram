namespace MyTelegram.QueryHandlers.InMemory.Draft;

public class GetDraftListByPeerQueryHandler(IQueryOnlyReadModelStore<DraftReadModel> store) : IQueryHandler<GetDraftListByPeerQuery, IReadOnlyCollection<IDraftReadModel>>
{
    public async Task<IReadOnlyCollection<IDraftReadModel>> ExecuteQueryAsync(GetDraftListByPeerQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FindAsync(
            p => p.OwnerPeerId == query.OwnerPeerId
                 && p.Peer.PeerType == query.PeerType
                 && p.Peer.PeerId == query.PeerId,
            cancellationToken: cancellationToken);
    }
}
