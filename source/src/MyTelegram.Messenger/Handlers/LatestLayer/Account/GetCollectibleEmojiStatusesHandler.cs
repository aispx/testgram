using MongoDB.Bson;
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
        var gifts = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .Find(d => d.OwnerUserId == input.UserId && !d.Burned)
            .SortBy(d => d.UniqueId)
            .ToListAsync();

        // One query for every model and pattern document instead of two per gift.
        var wantedDocumentIds = gifts
            .SelectMany(gift => new[] { GetModelDocumentId(gift), GetPatternDocumentId(gift) })
            .Where(id => id != 0)
            .Distinct()
            .ToList();
        var existingDocumentIds = await GetExistingDocumentIdsAsync(wantedDocumentIds);

        var statuses = new TVector<IEmojiStatus>();
        var documentIdsForHash = new List<long>();
        foreach (var gift in gifts)
        {
            var modelDocumentId = GetModelDocumentId(gift);
            if (!existingDocumentIds.Contains(modelDocumentId))
            {
                continue;
            }

            statuses.Add(CollectibleEmojiStatusHelper.ToEmojiStatus(
                gift,
                modelDocumentId,
                gift.Until,
                patternDocumentId => existingDocumentIds.Contains(patternDocumentId)));
            documentIdsForHash.Add(gift.UniqueId);
        }

        var hash = EmojiStatusesHelper.CalculateHash(documentIdsForHash);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TEmojiStatusesNotModified();
        }

        return new TEmojiStatuses { Hash = hash, Statuses = statuses };
    }

    private static long GetModelDocumentId(UniqueStarGiftDocument gift)
    {
        return gift.Attributes.FirstOrDefault(a => a.Type == "model")?.DocumentId ?? gift.DocumentId;
    }

    private static long GetPatternDocumentId(UniqueStarGiftDocument gift)
    {
        return gift.Attributes.FirstOrDefault(a => a.Type == "pattern")?.DocumentId ?? 0;
    }

    private async Task<HashSet<long>> GetExistingDocumentIdsAsync(IReadOnlyCollection<long> documentIds)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var docs = await mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel")
            .Find(Builders<BsonDocument>.Filter.In("DocumentId",
                documentIds.Select(p => (BsonValue)new BsonInt64(p))))
            .Project(Builders<BsonDocument>.Projection.Include("DocumentId"))
            .ToListAsync();

        return docs
            .Where(p => p.TryGetValue("DocumentId", out var value) && !value.IsBsonNull)
            .Select(p => p["DocumentId"].ToInt64())
            .ToHashSet();
    }
}
