namespace MyTelegram.QueryHandlers.MongoDB.User;

public class GetMaxUserIdQueryHandler(IQueryOnlyReadModelStore<UserReadModel> store) : IQueryHandler<GetMaxUserIdQuery, long>
{
    public async Task<long> ExecuteQueryAsync(GetMaxUserIdQuery query, CancellationToken cancellationToken)
    {
        // Bots live in their own id range above BotUserInitId. Including them here would drag the
        // regular-user sequence into the bot range, and every IsBotUser check would then treat
        // freshly registered users as bots.
        return await store.FirstOrDefaultAsync(
            p => p.UserId > 0 && p.UserId < MyTelegramConsts.BotUserInitId,
            createResult: p => p.UserId,
            sort: new SortOptions<UserReadModel>(p => p.UserId, SortType.Descending),
            cancellationToken: cancellationToken);
    }
}