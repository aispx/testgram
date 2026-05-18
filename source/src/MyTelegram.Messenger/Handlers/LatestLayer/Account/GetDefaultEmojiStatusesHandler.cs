using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get a list of default suggested <a href="https://corefork.telegram.org/api/emoji-status">emoji statuses</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getDefaultEmojiStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDefaultEmojiStatusesHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetDefaultEmojiStatuses, MyTelegram.Schema.Account.IEmojiStatuses>
{
    protected override async Task<MyTelegram.Schema.Account.IEmojiStatuses> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetDefaultEmojiStatuses obj)
    {
        return await BuildFromSetAsync("emoji_default_statuses", "StatusPack");
    }

    private async Task<TEmojiStatuses> BuildFromSetAsync(string slug, string fallbackShortName)
    {
        var set = await mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel")
            .Find(Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("Slug", slug),
                Builders<BsonDocument>.Filter.Eq("ShortName", fallbackShortName)))
            .FirstOrDefaultAsync();
        var statuses = new TVector<IEmojiStatus>();
        if (set?.TryGetValue("DocumentIds", out var ids) == true && ids.IsBsonArray)
        {
            foreach (var id in ids.AsBsonArray)
                statuses.Add(new TEmojiStatus { DocumentId = id.ToInt64() });
        }
        return new TEmojiStatuses { Statuses = statuses };
    }
}
