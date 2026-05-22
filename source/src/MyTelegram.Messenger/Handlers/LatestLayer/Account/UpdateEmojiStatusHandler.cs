using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Set an <a href="https://corefork.telegram.org/api/emoji-status">emoji status</a>
/// Possible errors
/// Code Type Description
/// 400 COLLECTIBLE_INVALID The specified collectible is invalid.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateEmojiStatusHandler(
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    MyTelegram.Services.Services.IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateEmojiStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateEmojiStatus obj)
    {
        long? documentId = null;
        int? until = null;
        long? collectibleId = null;
        UniqueStarGiftDocument? collectibleDoc = null;

        switch (obj.EmojiStatus)
        {
            case TEmojiStatus status:
                documentId = status.DocumentId;
                until = status.Until;
                break;
            case TEmojiStatusEmpty:
                break;
            case TInputEmojiStatusCollectible collectible:
            {
                var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .Find(d => d.OwnerUserId == input.UserId && d.UniqueId == collectible.CollectibleId && !d.Burned)
                    .FirstOrDefaultAsync();
                if (doc == null)
                    RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                collectibleDoc = doc;
                var model = doc.Attributes.FirstOrDefault(a => a.Type == "model");
                documentId = model?.DocumentId ?? doc.DocumentId;
                collectibleId = collectible.CollectibleId;
                until = collectible.Until;
                break;
            }
            default:
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                break;
        }

        var col = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        BsonValue emojiStatusDocumentValue = documentId.HasValue ? new BsonInt64(documentId.Value) : BsonNull.Value;
        BsonValue emojiStatusUntilValue = until.HasValue ? new BsonInt32(until.Value) : BsonNull.Value;
        var update = Builders<BsonDocument>.Update
            .Set("EmojiStatusDocumentId", emojiStatusDocumentValue)
            .Set("EmojiStatusValidUntil", emojiStatusUntilValue)
            .Set("EmojiStatusCollectibleId", (BsonValue)(collectibleId.HasValue ? new BsonInt64(collectibleId.Value) : BsonNull.Value));
        if (documentId.HasValue)
        {
            update = update.PushEach("RecentEmojiStatuses", [documentId.Value], slice: -10);
        }
        await col.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId), update);
        userAppService.InvalidateCache(input.UserId);

        IEmojiStatus resolvedStatus = documentId.HasValue
            ? collectibleDoc != null
                ? CollectibleEmojiStatusHelper.ToEmojiStatus(
                    collectibleDoc,
                    documentId.Value,
                    until,
                    patternDocumentId => CollectibleEmojiStatusHelper.DocumentExists(mongoDatabase, patternDocumentId))
                : new TEmojiStatus { DocumentId = documentId.Value, Until = until }
            : new TEmojiStatusEmpty();

        var emojiUpdate = new TUpdateUserEmojiStatus
        {
            UserId = input.UserId,
            EmojiStatus = resolvedStatus
        };
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(emojiUpdate),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, input.UserId),
            updates);

        var recentUpdate = new TUpdateRecentEmojiStatuses();
        var recentUpdates = new TUpdates
        {
            Updates = new TVector<IUpdate>(recentUpdate),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, input.UserId),
            recentUpdates);

        return new TBoolTrue();
    }
}
