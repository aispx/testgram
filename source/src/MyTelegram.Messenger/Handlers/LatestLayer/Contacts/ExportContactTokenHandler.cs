using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Generates a <a href="https://corefork.telegram.org/api/links#temporary-profile-links">temporary profile link</a> for the currently logged-in user.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.exportContactToken"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ExportContactTokenHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestExportContactToken, MyTelegram.Schema.IExportedContactToken>
{
    protected override async Task<MyTelegram.Schema.IExportedContactToken> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestExportContactToken obj)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(24));
        var expires = CurrentDate + 24 * 60 * 60;
        var collection = mongoDatabase.GetCollection<BsonDocument>("contact_tokens");
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAt"),
            new CreateIndexOptions { Name = "contact_tokens_expires_at_ttl", ExpireAfter = TimeSpan.Zero }));
        await collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", token),
            new BsonDocument
            {
                ["_id"] = token,
                ["UserId"] = input.UserId,
                ["Expires"] = expires,
                ["ExpiresAt"] = DateTime.UtcNow.AddSeconds(24 * 60 * 60),
                ["Date"] = CurrentDate,
            },
            new ReplaceOptions { IsUpsert = true });

        return new TExportedContactToken
        {
            Url = $"https://t.me/contact/{token}",
            Expires = expires,
        };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
