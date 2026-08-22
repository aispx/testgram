using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passport;

/// <param name="ReadModel">The bot's user read model, for building the <c>users</c> vector.</param>
/// <param name="PrivacyPolicyUrl">The service's privacy policy, shown before the user shares anything.</param>
public readonly record struct PassportBot(IUserReadModel ReadModel, string? PrivacyPolicyUrl);

public interface IPassportBotResolver
{
    /// <summary>
    /// Resolves the bot a passport authorization request is addressed to and checks that the public key
    /// quoted in the request is the one the bot registered through BotFather.
    /// Throws BOT_INVALID when the id is not a bot, PUBLIC_KEY_REQUIRED when the bot has no Passport
    /// public key or the quoted key is not it.
    /// </summary>
    Task<PassportBot> ResolveAsync(long botId, string? publicKey);

    /// <summary>The bot's registered Passport public key, or null when Passport is not enabled for it.</summary>
    Task<string?> GetPublicKeyAsync(long botId);

    /// <summary>Stores (or, with a null key, clears) the bot's Passport public key.</summary>
    Task SetPublicKeyAsync(long botId, string? normalizedPublicKey);
}

public class PassportBotResolver(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : IPassportBotResolver, ISingletonDependency
{
    private const string BotCollection = "botfather-bot-state";
    private const string PublicKeyField = "PassportPublicKey";
    private const string PrivacyPolicyField = "PrivacyPolicyUrl";

    public async Task<PassportBot> ResolveAsync(long botId, string? publicKey)
    {
        var readModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botId));
        if (readModel == null || !readModel.Bot || readModel.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var state = await GetStateAsync(botId);
        var storedKey = GetString(state, PublicKeyField);

        // Telegram answers PUBLIC_KEY_REQUIRED both when the bot never enabled Passport and when the
        // caller quoted a key that is not the bot's - a service must not be able to have documents
        // encrypted to a key it does not own.
        if (!PassportPublicKey.Matches(storedKey, publicKey))
        {
            RpcErrors.RpcErrors400.PublicKeyRequired.ThrowRpcError();
        }

        return new PassportBot(readModel!, GetString(state, PrivacyPolicyField));
    }

    public async Task<string?> GetPublicKeyAsync(long botId)
    {
        return GetString(await GetStateAsync(botId), PublicKeyField);
    }

    public async Task SetPublicKeyAsync(long botId, string? normalizedPublicKey)
    {
        var update = normalizedPublicKey == null
            ? Builders<BsonDocument>.Update.Unset(PublicKeyField)
            : Builders<BsonDocument>.Update.Set(PublicKeyField, normalizedPublicKey);

        await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("BotUserId", botId), update);
    }

    private async Task<BsonDocument?> GetStateAsync(long botId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("BotUserId", botId))
            .FirstOrDefaultAsync();
    }

    private static string? GetString(BsonDocument? document, string field)
    {
        if (document != null && document.TryGetValue(field, out var value) && value.IsString)
        {
            var text = value.AsString;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }
}
