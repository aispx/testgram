using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Fetch the full list of only the IDs of <a href="https://corefork.telegram.org/api/profile#music">songs currently added to the profile, see here »</a> for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getSavedMusicIds"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedMusicIdsHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetSavedMusicIds, MyTelegram.Schema.Account.ISavedMusicIds>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Account.ISavedMusicIds> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetSavedMusicIds obj)
    {
        // Query saved music IDs from MongoDB
        var collection = database.GetCollection<BsonDocument>("saved_music");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var sort = Builders<BsonDocument>.Sort.Ascending("Order"); // Sort by Order field

        var docs = await collection.Find(filter)
            .Sort(sort)
            .ToListAsync();

        var ids = new TVector<long>();
        foreach (var doc in docs)
        {
            ids.Add(doc["DocumentId"].AsInt64);
        }

        return new TSavedMusicIds
        {
            Ids = ids
        };
    }
}