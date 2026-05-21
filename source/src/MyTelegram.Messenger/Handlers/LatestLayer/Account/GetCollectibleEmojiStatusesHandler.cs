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
            var model = doc.Attributes.FirstOrDefault(a => a.Type == "model");
            var pattern = doc.Attributes.FirstOrDefault(a => a.Type == "pattern");
            var backdrop = doc.Attributes.FirstOrDefault(a => a.Type == "backdrop");
            var collectible = doc.Attributes.FirstOrDefault(a => a.Type == "collectible");
            statuses.Add(new TEmojiStatusCollectible
            {
                CollectibleId = collectible?.CollectibleId ?? doc.UniqueId,
                DocumentId = doc.DocumentId,
                Title = $"{doc.Title} #{doc.Num}",
                Slug = doc.Slug,
                PatternDocumentId = pattern?.DocumentId ?? 0,
                CenterColor = backdrop?.CenterColor ?? 0,
                EdgeColor = backdrop?.EdgeColor ?? 0,
                PatternColor = backdrop?.PatternColor ?? 0,
                TextColor = backdrop?.TextColor ?? 0,
                Until = doc.Until,
                Flags = doc.Until.HasValue ? 1 : 0,
            });
        }

        return new TEmojiStatuses { Statuses = statuses };
    }
}
