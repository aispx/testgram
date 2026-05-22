using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Obtain a list of <a href="https://corefork.telegram.org/api/emoji-status">emoji statuses »</a> for owned <a href="https://corefork.telegram.org/api/gifts#collectible-gifts">collectible gifts</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getCollectibleEmojiStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetCollectibleEmojiStatusesHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetCollectibleEmojiStatuses, MyTelegram.Schema.Account.IEmojiStatuses>
{
    protected override async Task<MyTelegram.Schema.Account.IEmojiStatuses> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetCollectibleEmojiStatuses obj)
    {
        var docs = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .Find(d => d.OwnerUserId == input.UserId && !d.Burned)
            .ToListAsync();

        var statuses = new TVector<IEmojiStatus>();
        foreach (var doc in docs)
        {
            var modelDocumentId = doc.Attributes.FirstOrDefault(a => a.Type == "model")?.DocumentId ?? doc.DocumentId;
            if (!await CollectibleEmojiStatusHelper.DocumentExistsAsync(mongoDatabase, modelDocumentId))
            {
                continue;
            }

            statuses.Add(CollectibleEmojiStatusHelper.ToEmojiStatus(
                doc,
                modelDocumentId,
                doc.Until,
                patternDocumentId => CollectibleEmojiStatusHelper.DocumentExists(mongoDatabase, patternDocumentId)));
        }

        return new TEmojiStatuses { Statuses = statuses };
    }
}
