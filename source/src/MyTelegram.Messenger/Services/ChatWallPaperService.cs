using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Services;

/// <inheritdoc />
public sealed class ChatWallPaperService(IMongoDatabase database, IWallPaperCatalog catalog)
    : IChatWallPaperService, ITransientDependency
{
    private const string ChatWallPapersCollection = "chat_wallpapers";

    public async Task<(MyTelegram.Schema.IWallPaper? WallPaper, bool Overridden)> GetChatWallPaperAsync(long ownerId,
        Peer peer)
    {
        var doc = await Collection.Find(ChatFilter(ownerId, peer.PeerId)).FirstOrDefaultAsync();

        if (doc == null)
        {
            // Wallpapers used to live in a WallpaperId field on the dialog, without settings and
            // without the for_both flag. Read those so an existing chat keeps its wallpaper.
            var wallPaperId = peer.PeerType == PeerType.User
                ? await GetLegacyWallPaperIdAsync(ownerId, peer.PeerId)
                : null;

            return wallPaperId.HasValue
                ? (await GetWallPaperAsync(wallPaperId.Value, null), false)
                : (null, false);
        }

        var storedId = doc.GetValue("WallpaperId", BsonNull.Value);
        if (storedId.IsBsonNull)
        {
            return (null, false);
        }

        var settings = WallPaperSettingsSerializer.FromBson(doc.GetValue("Settings", BsonNull.Value));
        var overridden = doc.GetValue("Overridden", false).ToBoolean();

        return (await GetWallPaperAsync(storedId.ToInt64(), settings), overridden);
    }

    public async Task SetChatWallPaperAsync(long ownerId, Peer peer, long? wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings, bool overridden)
    {
        var writesLegacyField = peer.PeerType == PeerType.User;
        var previous = await Collection.Find(ChatFilter(ownerId, peer.PeerId)).FirstOrDefaultAsync();

        if (!wallPaperId.HasValue)
        {
            await Collection.DeleteOneAsync(ChatFilter(ownerId, peer.PeerId));
            if (writesLegacyField)
            {
                await SetLegacyWallPaperIdAsync(ownerId, peer.PeerId, null);
            }

            return;
        }

        var document = new BsonDocument
        {
            { "UserId", ownerId },
            { "PeerId", peer.PeerId },
            { "PeerType", (int)peer.PeerType },
            { "WallpaperId", wallPaperId.Value },
            { "Overridden", overridden },
            { "Settings", WallPaperSettingsSerializer.ToBson(settings) }
        };

        // Only revert needs this, and only the wallpaper being displaced is worth keeping: a chain of
        // previous wallpapers is not something any method can ask for.
        if (previous != null && !previous.GetValue("WallpaperId", BsonNull.Value).IsBsonNull)
        {
            document.Add("PreviousWallpaperId", previous["WallpaperId"].ToInt64());
            document.Add("PreviousSettings", previous.GetValue("Settings", BsonNull.Value));
        }

        await Collection.ReplaceOneAsync(ChatFilter(ownerId, peer.PeerId), document,
            new ReplaceOptions { IsUpsert = true });

        if (writesLegacyField)
        {
            // A settings-only wallpaper has no catalogue row, so the legacy field cannot express it.
            await SetLegacyWallPaperIdAsync(ownerId, peer.PeerId,
                wallPaperId.Value == 0 ? null : wallPaperId.Value);
        }
    }

    public async Task<MyTelegram.Schema.IWallPaper?> RevertChatWallPaperAsync(long ownerId, Peer peer)
    {
        var doc = await Collection.Find(ChatFilter(ownerId, peer.PeerId)).FirstOrDefaultAsync();
        var previousId = doc?.GetValue("PreviousWallpaperId", BsonNull.Value) ?? BsonNull.Value;

        if (previousId.IsBsonNull)
        {
            await SetChatWallPaperAsync(ownerId, peer, null, null, overridden: false);

            return null;
        }

        var previousSettings = WallPaperSettingsSerializer.FromBson(doc!.GetValue("PreviousSettings", BsonNull.Value));
        await SetChatWallPaperAsync(ownerId, peer, previousId.ToInt64(), previousSettings, overridden: false);

        return await GetWallPaperAsync(previousId.ToInt64(), previousSettings);
    }

    public async Task<long?> ResolveWallPaperIdAsync(MyTelegram.Schema.IInputWallPaper? inputWallPaper)
    {
        switch (inputWallPaper)
        {
            case null:
                return null;
            case MyTelegram.Schema.TInputWallPaperNoFile { Id: 0 }:
                return 0;
            case MyTelegram.Schema.TInputWallPaper inputWallPaperById:
                return NonZeroOrThrow(inputWallPaperById.Id);
            case MyTelegram.Schema.TInputWallPaperNoFile inputWallPaperNoFile:
                return NonZeroOrThrow(inputWallPaperNoFile.Id);
            case MyTelegram.Schema.TInputWallPaperSlug inputWallPaperSlug:
            {
                var row = await catalog.FindBySlugAsync(inputWallPaperSlug.Slug);
                if (row == null)
                {
                    RpcErrors.RpcErrors400.WallpaperNotFound.ThrowRpcError();
                }

                return row!.WallPaperId;
            }
            default:
                RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();

                return null;
        }
    }

    public async Task<MyTelegram.Schema.IWallPaper?> GetWallPaperAsync(long wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings)
    {
        // Id 0 is a wallpaper made of nothing but its settings — a channel fill wallpaper, identified by
        // its emoticon. There is no catalogue row to look up.
        if (wallPaperId == 0)
        {
            return settings == null ? null : catalog.BuildFill(0, settings);
        }

        var row = await catalog.FindByIdAsync(wallPaperId);

        // The settings chosen for this chat win; the ones the wallpaper was uploaded with are the
        // fallback, so a wallpaper picked without customization still renders as intended.
        return row == null ? null : await catalog.BuildAsync(row, selfUserId: 0, settings ?? row.Settings);
    }

    private IMongoCollection<BsonDocument> Collection =>
        database.GetCollection<BsonDocument>(ChatWallPapersCollection);

    private static long NonZeroOrThrow(long wallPaperId)
    {
        if (wallPaperId == 0)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        return wallPaperId;
    }

    private static FilterDefinition<BsonDocument> ChatFilter(long userId, long peerId)
    {
        return Builders<BsonDocument>.Filter.Eq("UserId", userId) &
               Builders<BsonDocument>.Filter.Eq("PeerId", peerId);
    }

    private async Task<long?> GetLegacyWallPaperIdAsync(long userId, long peerId)
    {
        var dialogId = DialogId.Create(userId, PeerType.User, peerId);
        var dialog = await database.GetCollection<BsonDocument>("eventflow-dialogreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", dialogId.Value))
            .FirstOrDefaultAsync();

        var value = dialog?.GetValue("WallpaperId", BsonNull.Value) ?? BsonNull.Value;

        return value.IsBsonNull ? null : value.ToInt64();
    }

    /// <summary>
    /// Keeps the legacy dialog field in step with the authoritative record, so anything still
    /// reading it does not serve a wallpaper that was already removed.
    /// </summary>
    private async Task SetLegacyWallPaperIdAsync(long userId, long peerId, long? wallPaperId)
    {
        var dialogId = DialogId.Create(userId, PeerType.User, peerId);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", dialogId.Value);
        var dialogs = database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");

        if (wallPaperId.HasValue)
        {
            await dialogs.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("WallpaperId", wallPaperId.Value),
                new UpdateOptions { IsUpsert = true });
        }
        else
        {
            await dialogs.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Unset("WallpaperId"));
        }
    }
}
