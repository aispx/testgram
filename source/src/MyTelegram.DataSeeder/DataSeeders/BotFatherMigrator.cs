using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.DataSeeder.DataSeeders;

/// <summary>
/// One-shot repair of the BotFather peer, taken over from the botfather-init container so that the
/// data seeder is the only thing that prepares the database.
/// <para>
/// It renames the bot-state collection of the fork this project came from, takes <c>@botfather</c> back
/// from any channel that grabbed it, drops username rows pointing somewhere other than the BotFather
/// user, and makes sure the username resolves to it. On an install that never had the legacy data every
/// step is a no-op, and BotFather itself is created through its aggregate by <see cref="UserDataSeeder"/>.
/// </para>
/// </summary>
public class BotFatherMigrator(
    IMongoDatabase database,
    ILogger<BotFatherMigrator> logger,
    IDataSeederHelper dataSeederHelper) : IDataSeeder, ITransientDependency
{
    private const string BotFatherUserName = "botfather";
    private const string LegacyUserName = "xiefather";
    private const string BotStateCollectionName = "botfather-bot-state";
    private const string LegacyBotStateCollectionName = "xiefather-bot-state";

    public async Task SeedAsync()
    {
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();
        if (config.IsBotFatherMigrated)
        {
            return;
        }

        await MoveLegacyBotStateAsync();
        await ReleaseUserNameFromChannelsAsync();
        await RepairUserNameAsync();

        config.IsBotFatherMigrated = true;
        await dataSeederHelper.SaveDataSeederConfigAsync();
        logger.LogInformation("BotFather migration completed");
    }

    private async Task MoveLegacyBotStateAsync()
    {
        var collectionNames = await (await database.ListCollectionNamesAsync()).ToListAsync();
        if (!collectionNames.Contains(LegacyBotStateCollectionName))
        {
            return;
        }

        var legacy = database.GetCollection<BsonDocument>(LegacyBotStateCollectionName);
        if (!collectionNames.Contains(BotStateCollectionName))
        {
            await database.RenameCollectionAsync(LegacyBotStateCollectionName, BotStateCollectionName);
            logger.LogInformation("Renamed the legacy bot state collection to {Collection}", BotStateCollectionName);

            return;
        }

        var current = database.GetCollection<BsonDocument>(BotStateCollectionName);
        var documents = await legacy.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();
        foreach (var document in documents)
        {
            await current.ReplaceOneAsync(Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
                document,
                new ReplaceOptions { IsUpsert = true });
        }

        await database.DropCollectionAsync(LegacyBotStateCollectionName);
        logger.LogInformation("Merged {Count} documents from the legacy bot state collection", documents.Count);
    }

    /// <summary>
    /// A channel holding <c>@botfather</c> would win username resolution over the bot itself.
    /// </summary>
    private async Task ReleaseUserNameFromChannelsAsync()
    {
        var result = await database.GetCollection<BsonDocument>("eventflow-channelreadmodel")
            .UpdateManyAsync(Builders<BsonDocument>.Filter.Eq("UserName", BotFatherUserName),
                Builders<BsonDocument>.Update.Set("UserName", BsonNull.Value));

        if (result.ModifiedCount > 0)
        {
            logger.LogInformation("Released @{UserName} from {Count} channels", BotFatherUserName, result.ModifiedCount);
        }
    }

    private async Task RepairUserNameAsync()
    {
        var collection = database.GetCollection<BsonDocument>("eventflow-usernamereadmodel");
        var builder = Builders<BsonDocument>.Filter;

        await collection.DeleteManyAsync(builder.And(
            builder.Eq("UserName", BotFatherUserName),
            builder.Ne("PeerId", MyTelegramConsts.BotFatherUserId)));
        await collection.DeleteManyAsync(builder.Eq("UserName", LegacyUserName));

        // Only repair the row when the user is actually there: on a fresh database the create-user saga
        // writes it, and pointing a username at a user that does not exist would break resolution.
        var userExists = await database.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(builder.Eq("UserId", MyTelegramConsts.BotFatherUserId))
            .AnyAsync();

        if (!userExists)
        {
            return;
        }

        await collection.UpdateOneAsync(builder.Eq("_id", $"username-{BotFatherUserName}"),
            Builders<BsonDocument>.Update
                .Set("UserName", BotFatherUserName)
                .Set("PeerId", MyTelegramConsts.BotFatherUserId)
                .Set("PeerType", (int)PeerType.User)
                .Set("IsActive", true),
            new UpdateOptions { IsUpsert = true });
    }
}
