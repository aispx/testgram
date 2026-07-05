using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Get bots that the current user owns (can edit)
/// See https://core.telegram.org/method/bots.getAdminedBots
/// </summary>
internal sealed class GetAdminedBotsHandler(
    IMongoDatabase database,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetAdminedBots, TVector<IUser>>
{
    protected override async Task<TVector<IUser>> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Bots.RequestGetAdminedBots obj)
    {
        // Find all bots where current user is the owner
        var collection = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Bot", true),
            Builders<BsonDocument>.Filter.Exists("CreatorUserId"),
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId)
        );

        var botDocs = await collection.Find(filter).ToListAsync();

        if (botDocs.Count == 0)
        {
            return new TVector<IUser>();
        }

        // Get full user info for each bot
        var botIds = botDocs.Select(d => d["UserId"].AsInt64).ToList();
        var users = await queryProcessor.ProcessAsync(
            new GetUsersByUserIdListQuery(botIds),
            CancellationToken.None
        );

        var result = new TVector<IUser>();
        foreach (var user in users)
        {
            var converted = userConverterService.ToUser(input, user, layer: input.Layer);
            if (converted is TUser tUser)
            {
                tUser.BotCanEdit = true;
            }

            result.Add(converted);
        }

        return result;
    }
}
