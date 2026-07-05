using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Initialize a <a href="https://corefork.telegram.org/api/takeout">takeout session, see here » for more info</a>.
/// Possible errors
/// Code Type Description
/// 420 TAKEOUT_INIT_DELAY_%d Sorry, for security reasons, you will be able to begin downloading your data in %d seconds. We have notified all your devices about the export request to make sure it's authorized and to give you time to react if it's not.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.initTakeoutSession"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InitTakeoutSessionHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestInitTakeoutSession, MyTelegram.Schema.Account.ITakeout>
{
    protected override async Task<MyTelegram.Schema.Account.ITakeout> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestInitTakeoutSession obj)
    {
        var id = CreateTakeoutId();
        var expiresAt = DateTime.UtcNow.AddDays(7);
        await mongoDatabase.GetCollection<BsonDocument>("takeout_sessions").ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            new BsonDocument
            {
                ["_id"] = id,
                ["UserId"] = input.UserId,
                ["Contacts"] = obj.Contacts,
                ["MessageUsers"] = obj.MessageUsers,
                ["MessageChats"] = obj.MessageChats,
                ["MessageMegagroups"] = obj.MessageMegagroups,
                ["MessageChannels"] = obj.MessageChannels,
                ["Files"] = obj.Files,
                ["FileMaxSize"] = obj.FileMaxSize ?? 0,
                ["Date"] = CurrentDate,
                ["ExpiresAt"] = expiresAt,
                ["Active"] = true,
            },
            new ReplaceOptions { IsUpsert = true });

        return new TTakeout { Id = id };
    }

    private static long CreateTakeoutId()
    {
        var bytes = RandomNumberGenerator.GetBytes(sizeof(long));
        var id = BitConverter.ToInt64(bytes) & long.MaxValue;
        return id == 0 ? 1 : id;
    }
}
