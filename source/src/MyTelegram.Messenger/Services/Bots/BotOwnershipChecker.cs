using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Answers whether a user owns a bot. Methods that let a user act on behalf of a bot -
/// <c>bots.setBotInfo</c>, <c>bots.setCustomVerification</c> - are otherwise wide open: being a bot
/// is not the same as being <em>your</em> bot.
/// </summary>
public interface IBotOwnershipChecker
{
    /// <summary>
    /// A bot is owned by the user recorded in <c>bot-owners</c>, or by the <c>CreatorUserId</c> on
    /// the bot's user read model - both are written depending on how the bot was registered.
    /// </summary>
    Task<bool> IsOwnerAsync(long botUserId, long userId);
}

/// <inheritdoc />
public class BotOwnershipChecker(IMongoDatabase mongoDatabase) : IBotOwnershipChecker, ITransientDependency
{
    public async Task<bool> IsOwnerAsync(long botUserId, long userId)
    {
        var ownedViaBotOwners = await mongoDatabase.GetCollection<BsonDocument>("bot-owners")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("OwnerId", userId))
            .Limit(1)
            .AnyAsync();
        if (ownedViaBotOwners)
        {
            return true;
        }

        return await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("CreatorUserId", userId))
            .Limit(1)
            .AnyAsync();
    }
}
