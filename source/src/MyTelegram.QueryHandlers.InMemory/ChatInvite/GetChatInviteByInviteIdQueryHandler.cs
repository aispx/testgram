namespace MyTelegram.QueryHandlers.InMemory.ChatInvite;

public class GetChatInviteByInviteIdQueryHandler(IQueryOnlyReadModelStore<ChatInviteReadModel> store) : IQueryHandler<GetChatInviteByInviteIdQuery, IChatInviteReadModel?>
{
    public async Task<IChatInviteReadModel?> ExecuteQueryAsync(GetChatInviteByInviteIdQuery query, CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(p => p.PeerId == query.PeerId && p.InviteId == query.InviteId, cancellationToken: cancellationToken);
    }
}
