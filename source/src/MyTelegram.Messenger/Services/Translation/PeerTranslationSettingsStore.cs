using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Translation;

/// <summary>
/// The per-account, per-peer "do not offer to translate this chat" flag behind
/// <c>messages.togglePeerTranslations</c>, reported back as the <c>translations_disabled</c> flag of
/// <c>userFull</c>, <c>chatFull</c> and <c>channelFull</c>.
///
/// <para>It belongs to the caller, not to the peer: dismissing the popup in a chat is one user's
/// decision, and the API's own words are that the flag "signals to the other sessions that the
/// autotranslation popup should not be displayed". Android reads it from the full info
/// (<c>TranslateController.isTranslateDialogHidden</c>) and writes it through this method
/// (<c>setHideTranslateDialog</c>), so a server that stores nothing — which is what this used to do —
/// makes the popup come back on every other device forever.</para>
/// See https://corefork.telegram.org/method/messages.togglePeerTranslations
/// </summary>
public interface IPeerTranslationSettingsStore
{
    Task SetAsync(long userId, Peer peer, bool disabled, CancellationToken cancellationToken = default);

    Task<bool> IsDisabledAsync(long userId, Peer peer, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class PeerTranslationSettingsStore(IMongoDatabase mongoDatabase)
    : IPeerTranslationSettingsStore, ITransientDependency
{
    public const string CollectionName = "peer_translations_disabled";

    private IMongoCollection<BsonDocument> Collection =>
        mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    /// <summary>
    /// The row id. Both sides must build it the same way, which is why the peer arrives already
    /// normalised by <see cref="IPeerHelper"/>: <c>inputPeerSelf</c> resolves to
    /// <see cref="PeerType.Self"/>, and a read path that took it as <see cref="PeerType.User"/> would
    /// address a different row and report the default back — the trap
    /// <c>account.getNotifySettings</c> fell into.
    /// </summary>
    private static string BuildId(long userId, Peer peer) => $"{userId}:{peer.PeerType}:{peer.PeerId}";

    public Task SetAsync(long userId, Peer peer, bool disabled, CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", BuildId(userId, peer)),
            Builders<BsonDocument>.Update
                .Set("UserId", userId)
                .Set("PeerType", (int)peer.PeerType)
                .Set("PeerId", peer.PeerId)
                .Set("Disabled", disabled)
                .Set("Date", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<bool> IsDisabledAsync(long userId, Peer peer, CancellationToken cancellationToken = default)
    {
        var document = await Collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", BuildId(userId, peer)))
            .FirstOrDefaultAsync(cancellationToken);

        return document != null
               && document.GetValue("Disabled", BsonBoolean.False) is { BsonType: BsonType.Boolean } value
               && value.AsBoolean;
    }
}
