using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Tracks the phone numbers and email addresses a user verified for Telegram Passport through
/// <c>account.verifyPhone</c> / <c>account.verifyEmail</c>. A <c>securePlainPhone</c> /
/// <c>securePlainEmail</c> may only be saved once the value has passed that check — the whole point of
/// the plain constructors is that the service receives an address Telegram already verified.
/// See https://corefork.telegram.org/passport/encryption#securePlainData
/// </summary>
public interface IPassportVerificationStore
{
    Task SetPhoneVerifiedAsync(long userId, string phoneNumber);

    Task<bool> IsPhoneVerifiedAsync(long userId, string phoneNumber);

    Task SetEmailVerifiedAsync(long userId, string email);

    Task<bool> IsEmailVerifiedAsync(long userId, string email);

    Task ClearAsync(long userId);
}

public class PassportVerificationStore(IMongoDatabase mongoDatabase)
    : IPassportVerificationStore, ISingletonDependency
{
    private const string PhoneCollection = "passport_phones";
    private const string EmailCollection = "passport_emails";

    public Task SetPhoneVerifiedAsync(long userId, string phoneNumber) =>
        UpsertAsync(PhoneCollection, "Phone", $"passport-phone-{userId}", userId, Normalize(phoneNumber));

    public Task<bool> IsPhoneVerifiedAsync(long userId, string phoneNumber) =>
        MatchesAsync(PhoneCollection, "Phone", userId, Normalize(phoneNumber));

    public Task SetEmailVerifiedAsync(long userId, string email) =>
        UpsertAsync(EmailCollection, "Email", $"passport-email-{userId}", userId, Normalize(email));

    public Task<bool> IsEmailVerifiedAsync(long userId, string email) =>
        MatchesAsync(EmailCollection, "Email", userId, Normalize(email));

    public async Task ClearAsync(long userId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        await mongoDatabase.GetCollection<BsonDocument>(PhoneCollection).DeleteManyAsync(filter);
        await mongoDatabase.GetCollection<BsonDocument>(EmailCollection).DeleteManyAsync(filter);
    }

    private async Task UpsertAsync(string collectionName, string field, string id, long userId, string value)
    {
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["UserId"] = userId,
            [field] = value,
            ["VerifiedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await mongoDatabase.GetCollection<BsonDocument>(collectionName).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            document,
            new ReplaceOptions { IsUpsert = true });
    }

    private async Task<bool> MatchesAsync(string collectionName, string field, long userId, string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        return await mongoDatabase.GetCollection<BsonDocument>(collectionName)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq(field, value)))
            .AnyAsync();
    }

    /// <summary>
    /// Phone numbers are compared without the leading "+" or separators and emails case-insensitively,
    /// so a value the user verified in one form still matches when saveSecureValue quotes another.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();

        return trimmed.Contains('@')
            ? trimmed.ToLowerInvariant()
            : new string(trimmed.Where(char.IsDigit).ToArray());
    }
}
