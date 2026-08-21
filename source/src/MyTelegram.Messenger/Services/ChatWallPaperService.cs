using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// The per-chat wallpaper set with <c>messages.setChatWallPaper</c>.
/// <para>
/// It is reported back as <c>userFull.wallpaper</c> and kept fresh with <c>updatePeerWallpaper</c>,
/// see <a href="https://corefork.telegram.org/api/wallpapers#installing-wallpapers-in-a-specific-chat-or-channel">wallpapers »</a>
/// and <a href="https://corefork.telegram.org/api/peers#handling-updates">peer database »</a>.
/// </para>
/// </summary>
public interface IChatWallPaperService
{
    /// <summary>
    /// The wallpaper <paramref name="ownerId"/> sees in the chat with <paramref name="peer"/>, and
    /// whether it was chosen by the other side with <c>for_both</c>. A channel wallpaper belongs to
    /// the channel itself, so it is stored and read with the channel as its own owner.
    /// </summary>
    Task<(MyTelegram.Schema.IWallPaper? WallPaper, bool Overridden)> GetChatWallPaperAsync(long ownerId, Peer peer);

    /// <inheritdoc cref="GetChatWallPaperAsync(long, Peer)"/>
    Task SetChatWallPaperAsync(long ownerId, Peer peer, long? wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings, bool overridden);

    /// <summary>
    /// Resolves an <c>inputWallPaper</c> to its stored id, raising the documented errors for an
    /// unknown slug or a zero id.
    /// </summary>
    Task<long?> ResolveWallPaperIdAsync(MyTelegram.Schema.IInputWallPaper? inputWallPaper);

    /// <summary>
    /// Builds the wallpaper constructor, applying the per-chat <paramref name="settings"/> on top of
    /// the ones the wallpaper was uploaded with.
    /// </summary>
    Task<MyTelegram.Schema.IWallPaper?> GetWallPaperAsync(long wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings);
}

public sealed class ChatWallPaperService(IMongoDatabase database) : IChatWallPaperService, ITransientDependency
{
    private const string ChatWallPapersCollection = "chat_wallpapers";
    private const string WallPapersCollection = "wallpapers";

    public async Task<(MyTelegram.Schema.IWallPaper? WallPaper, bool Overridden)> GetChatWallPaperAsync(long ownerId,
        Peer peer)
    {
        var doc = await database.GetCollection<BsonDocument>(ChatWallPapersCollection)
            .Find(ChatFilter(ownerId, peer.PeerId))
            .FirstOrDefaultAsync();

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

        var settings = ToWallPaperSettings(doc.GetValue("Settings", BsonNull.Value));
        var overridden = doc.GetValue("Overridden", false).ToBoolean();

        return (await GetWallPaperAsync(storedId.ToInt64(), settings), overridden);
    }

    public async Task SetChatWallPaperAsync(long ownerId, Peer peer, long? wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings, bool overridden)
    {
        var collection = database.GetCollection<BsonDocument>(ChatWallPapersCollection);
        var writesLegacyField = peer.PeerType == PeerType.User;

        if (!wallPaperId.HasValue)
        {
            await collection.DeleteOneAsync(ChatFilter(ownerId, peer.PeerId));
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
            { "Settings", ToBson(settings) }
        };

        await collection.ReplaceOneAsync(ChatFilter(ownerId, peer.PeerId), document,
            new ReplaceOptions { IsUpsert = true });

        if (writesLegacyField)
        {
            await SetLegacyWallPaperIdAsync(ownerId, peer.PeerId, wallPaperId);
        }
    }

    public async Task<long?> ResolveWallPaperIdAsync(MyTelegram.Schema.IInputWallPaper? inputWallPaper)
    {
        switch (inputWallPaper)
        {
            case null:
            case MyTelegram.Schema.TInputWallPaperNoFile { Id: 0 }:
                return null;
            case MyTelegram.Schema.TInputWallPaper inputWallPaperById:
                return NonZeroOrThrow(inputWallPaperById.Id);
            case MyTelegram.Schema.TInputWallPaperNoFile inputWallPaperNoFile:
                return NonZeroOrThrow(inputWallPaperNoFile.Id);
            case MyTelegram.Schema.TInputWallPaperSlug inputWallPaperSlug:
            {
                var doc = await database.GetCollection<BsonDocument>(WallPapersCollection)
                    .Find(Builders<BsonDocument>.Filter.Eq("Slug", inputWallPaperSlug.Slug))
                    .FirstOrDefaultAsync();

                if (doc == null)
                {
                    RpcErrors.RpcErrors400.WallpaperNotFound.ThrowRpcError();
                }

                return doc!["WallpaperId"].ToInt64();
            }
            default:
                RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();

                return null;
        }
    }

    public async Task<MyTelegram.Schema.IWallPaper?> GetWallPaperAsync(long wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings)
    {
        var doc = await database.GetCollection<BsonDocument>(WallPapersCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("WallpaperId", wallPaperId))
            .FirstOrDefaultAsync();

        if (doc == null)
        {
            return null;
        }

        // The settings chosen for this chat win; the ones the wallpaper was uploaded with are the
        // fallback, so a wallpaper picked without customization still renders as intended.
        var effectiveSettings = settings ?? ToWallPaperSettings(doc.GetValue("Settings", BsonNull.Value));
        var documentId = doc.GetValue("DocumentId", 0L).ToInt64();

        if (documentId == 0)
        {
            return new MyTelegram.Schema.TWallPaperNoFile
            {
                Id = wallPaperId,
                Default = doc.GetValue("IsDefault", false).ToBoolean(),
                Dark = doc.GetValue("IsDark", false).ToBoolean(),
                Settings = effectiveSettings
            };
        }

        var documentDoc = await database.GetCollection<BsonDocument>("eventflow-documentreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", documentId))
            .FirstOrDefaultAsync();

        if (documentDoc == null)
        {
            return null;
        }

        return new MyTelegram.Schema.TWallPaper
        {
            Id = wallPaperId,
            AccessHash = doc.GetValue("AccessHash", 0L).ToInt64(),
            Slug = doc.GetValue("Slug", string.Empty).AsString,
            Default = doc.GetValue("IsDefault", false).ToBoolean(),
            Pattern = doc.GetValue("IsPattern", false).ToBoolean(),
            Dark = doc.GetValue("IsDark", false).ToBoolean(),
            Document = ToDocument(documentDoc),
            Settings = effectiveSettings
        };
    }

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

    private static BsonValue ToBson(MyTelegram.Schema.IWallPaperSettings? settings)
    {
        if (settings is not MyTelegram.Schema.TWallPaperSettings wallPaperSettings)
        {
            return BsonNull.Value;
        }

        var doc = new BsonDocument
        {
            { "Blur", wallPaperSettings.Blur },
            { "Motion", wallPaperSettings.Motion }
        };

        AddIfSet(doc, "BackgroundColor", wallPaperSettings.BackgroundColor);
        AddIfSet(doc, "SecondBackgroundColor", wallPaperSettings.SecondBackgroundColor);
        AddIfSet(doc, "ThirdBackgroundColor", wallPaperSettings.ThirdBackgroundColor);
        AddIfSet(doc, "FourthBackgroundColor", wallPaperSettings.FourthBackgroundColor);
        AddIfSet(doc, "Intensity", wallPaperSettings.Intensity);
        AddIfSet(doc, "Rotation", wallPaperSettings.Rotation);

        if (!string.IsNullOrEmpty(wallPaperSettings.Emoticon))
        {
            doc.Add("Emoticon", wallPaperSettings.Emoticon);
        }

        return doc;
    }

    private static void AddIfSet(BsonDocument doc, string name, int? value)
    {
        if (value.HasValue)
        {
            doc.Add(name, value.Value);
        }
    }

    private static MyTelegram.Schema.IWallPaperSettings? ToWallPaperSettings(BsonValue value)
    {
        if (value.IsBsonNull || !value.IsBsonDocument)
        {
            return null;
        }

        var doc = value.AsBsonDocument;

        return new MyTelegram.Schema.TWallPaperSettings
        {
            Blur = doc.GetValue("Blur", false).ToBoolean(),
            Motion = doc.GetValue("Motion", false).ToBoolean(),
            BackgroundColor = GetInt32(doc, "BackgroundColor"),
            SecondBackgroundColor = GetInt32(doc, "SecondBackgroundColor"),
            ThirdBackgroundColor = GetInt32(doc, "ThirdBackgroundColor"),
            FourthBackgroundColor = GetInt32(doc, "FourthBackgroundColor"),
            Intensity = GetInt32(doc, "Intensity"),
            Rotation = GetInt32(doc, "Rotation"),
            Emoticon = doc.Contains("Emoticon") && !doc["Emoticon"].IsBsonNull ? doc["Emoticon"].AsString : null
        };
    }

    private static int? GetInt32(BsonDocument doc, string name)
    {
        return doc.Contains(name) && !doc[name].IsBsonNull ? doc[name].ToInt32() : null;
    }

    private static MyTelegram.Schema.IDocument ToDocument(BsonDocument doc)
    {
        var fileReference = doc.GetValue("FileReference", BsonNull.Value) switch
        {
            { BsonType: BsonType.Binary } binary => binary.AsBsonBinaryData.Bytes,
            { BsonType: BsonType.Array } array => array.AsBsonArray.Select(p => (byte)p.ToInt32()).ToArray(),
            _ => []
        };

        return new MyTelegram.Schema.TDocument
        {
            Id = doc["DocumentId"].ToInt64(),
            AccessHash = doc.GetValue("AccessHash", 0L).ToInt64(),
            FileReference = fileReference,
            Date = doc.GetValue("Date", 0).ToInt32(),
            MimeType = doc.GetValue("MimeType", "image/jpeg").AsString,
            Size = doc.GetValue("Size", 0L).ToInt64(),
            Thumbs = new TVector<MyTelegram.Schema.IPhotoSize>(),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = doc.GetValue("DcId", 2).ToInt32(),
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>()
        };
    }
}
