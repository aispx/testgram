using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Services.Impl;

/// <inheritdoc cref="IEmojiStatusInputResolver"/>
public class EmojiStatusInputResolver(IMongoDatabase mongoDatabase)
    : IEmojiStatusInputResolver, ITransientDependency
{
    private readonly IMongoCollection<UniqueStarGiftDocument> _giftCollection =
        mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

    public async Task<EmojiStatus?> ResolveAsync(IEmojiStatus? emojiStatus, long ownerUserId)
    {
        switch (emojiStatus)
        {
            case null:
            case TEmojiStatusEmpty:
                return null;

            case TEmojiStatus status:
                if (status.DocumentId == 0)
                {
                    RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                }

                return new EmojiStatus(status.DocumentId, status.Until);

            case TInputEmojiStatusCollectible collectible:
            {
                var gift = await _giftCollection
                    .Find(d => d.UniqueId == collectible.CollectibleId
                               && d.OwnerUserId == ownerUserId
                               && !d.Burned)
                    .FirstOrDefaultAsync();
                if (gift == null)
                {
                    RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                }

                // The emoji shown is the gift's model attribute.
                var documentId = gift!.Attributes.FirstOrDefault(a => a.Type == "model")?.DocumentId
                                 ?? gift.DocumentId;

                return new EmojiStatus(documentId, collectible.Until, collectible.CollectibleId);
            }

            default:
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                return null;
        }
    }
}
