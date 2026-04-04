using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;
/// <summary>
/// Check whether the given short name is available
/// Possible errors
/// Code Type Description
/// 400 SHORT_NAME_INVALID The specified short name is invalid.
/// 400 SHORT_NAME_OCCUPIED The specified short name is already in use.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.checkShortName"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckShortNameHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestCheckShortName, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stickers.RequestCheckShortName obj)
    {
        var shortName = obj.ShortName?.Trim() ?? "";

        // Validate format: must start with letter, contain only letters/digits/underscores, 1-64 chars
        if (string.IsNullOrEmpty(shortName) || !Regex.IsMatch(shortName, @"^[a-zA-Z][a-zA-Z0-9_]{0,63}$"))
        {
            RpcErrors.RpcErrors400.ShortNameInvalid.ThrowRpcError();
        }

        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");

        // Check if short name is already taken
        var existingSet = await setCol.Find(Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("ShortName", shortName),
            Builders<BsonDocument>.Filter.Eq("Slug", shortName)
        )).FirstOrDefaultAsync();

        if (existingSet != null)
        {
            RpcErrors.RpcErrors400.ShortNameOccupied.ThrowRpcError();
        }

        // Short name is available
        return new TBoolTrue();
    }
}