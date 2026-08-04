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
internal sealed class GetDefaultEmojiStatusesHandler(
    IAppConfigHelper appConfigHelper,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetDefaultEmojiStatuses, MyTelegram.Schema.Account.IEmojiStatuses>
{
    protected override async Task<MyTelegram.Schema.Account.IEmojiStatuses> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetDefaultEmojiStatuses obj)
    {
        // The set advertised to clients in the app config wins; the slug and the legacy short name
        // are kept as fallbacks so an unconfigured server still serves its status pack.
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq("Slug", "emoji_default_statuses"),
            Builders<BsonDocument>.Filter.Eq("ShortName", "StatusPack")
        };
        if (appConfigHelper.GetInt64Value("default_emoji_statuses_stickerset_id") is { } stickerSetId)
        {
            filters.Insert(0, Builders<BsonDocument>.Filter.Eq("StickerSetId", new BsonInt64(stickerSetId)));
        }

        var documentIds = await EmojiStatusesHelper.GetDocumentIdsAsync(mongoDatabase,
            Builders<BsonDocument>.Filter.Or(filters));

        return EmojiStatusesHelper.ToEmojiStatuses(documentIds, obj.Hash);
    }
}
