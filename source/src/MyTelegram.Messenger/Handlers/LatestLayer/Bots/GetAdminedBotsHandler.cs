using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Get a list of bots owned by the current user
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getAdminedBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAdminedBotsHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetAdminedBots, TVector<MyTelegram.Schema.IUser>>
{
    protected override async Task<TVector<MyTelegram.Schema.IUser>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetAdminedBots obj)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("IsBot", true)
        );

        var botDocs = await collection.Find(filter).ToListAsync();
        var botIds = botDocs.Select(d => d["UserId"].AsInt64).ToList();

        if (botIds.Count == 0)
            return new TVector<IUser>();

        var users = await userConverterService.GetUserListAsync(input, botIds, false, false, input.Layer);
        return new TVector<IUser>(users);
    }
}