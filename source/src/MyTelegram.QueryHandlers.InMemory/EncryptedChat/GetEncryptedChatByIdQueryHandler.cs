namespace MyTelegram.QueryHandlers.InMemory.EncryptedChat;

public class GetEncryptedChatByIdQueryHandler(IQueryOnlyReadModelStore<EncryptedChatReadModel> store)
    : IQueryHandler<GetEncryptedChatByIdQuery, IEncryptedChatReadModel?>
{
    public async Task<IEncryptedChatReadModel?> ExecuteQueryAsync(GetEncryptedChatByIdQuery query,
        CancellationToken cancellationToken)
    {
        return await store.FirstOrDefaultAsync(p => p.ChatId == query.ChatId, cancellationToken);
    }
}
