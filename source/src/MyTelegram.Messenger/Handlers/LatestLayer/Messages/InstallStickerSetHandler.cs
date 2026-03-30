using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Messages;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class InstallStickerSetHandler(
    IMongoDatabase mongoDatabase,
    ILogger<InstallStickerSetHandler> logger) : RpcResultObjectHandler<RequestInstallStickerSet, IStickerSetInstallResult>
{
    private static readonly Dictionary<string, string> DiceSlugMap = new()
    {
        ["🎲"] = "dice_🎲",
        ["🎯"] = "dice_🎯",
        ["🏀"] = "dice_🏀",
        ["⚽"] = "dice_⚽",
        ["⚽️"] = "dice_⚽",
        ["🎰"] = "dice_🎰",
        ["🎳"] = "dice_🎳",
    };

    private static readonly Dictionary<Type, string> SpecialSetSlugMap = new()
    {
        [typeof(TInputStickerSetAnimatedEmoji)] = "animated_emoji",
        [typeof(TInputStickerSetAnimatedEmojiAnimations)] = "animated_emoji_animations",
        [typeof(TInputStickerSetPremiumGifts)] = "premium_gifts",
        [typeof(TInputStickerSetEmojiGenericAnimations)] = "emoji_generic_animations",
        [typeof(TInputStickerSetEmojiDefaultStatuses)] = "emoji_default_statuses",
        [typeof(TInputStickerSetEmojiDefaultTopicIcons)] = "emoji_default_topic_icons",
        [typeof(TInputStickerSetEmojiChannelDefaultStatuses)] = "emoji_channel_statuses",
    };

    protected override async Task<IStickerSetInstallResult> HandleCoreAsync(IRequestInput input, RequestInstallStickerSet obj)
    {
        Console.WriteLine($"[DEBUG] InstallStickerSetHandler called: Type={obj.Stickerset.GetType().Name}, UserId={input.UserId}");
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        BsonDocument? setDoc = null;

        if (obj.Stickerset is TInputStickerSetDice dice)
        {
            var slug = DiceSlugMap.GetValueOrDefault(dice.Emoticon) ?? "";
            setDoc = await FindSetBySlugAsync(setCol, slug);
        }
        else if (SpecialSetSlugMap.TryGetValue(obj.Stickerset.GetType(), out var slug))
        {
            setDoc = await FindSetBySlugAsync(setCol, slug);
        }
        else if (obj.Stickerset is TInputStickerSetID setById)
        {
            setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", setById.Id)).FirstOrDefaultAsync();
        }
        else if (obj.Stickerset is TInputStickerSetShortName shortNameSet)
        {
            setDoc = await FindSetBySlugAsync(setCol, shortNameSet.ShortName);
        }

        if (setDoc == null)
        {
            logger.LogWarning("Stickerset not found for install");
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var setId = GetInt64(setDoc["StickerSetId"]);
        var setAccessHash = GetInt64(setDoc["AccessHash"]);

        var userSetCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userinstalledstickersetreadmodel");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId)
        );

        var existing = await userSetCol.Find(filter).FirstOrDefaultAsync();
        if (existing == null)
        {
            var userSetId = ObjectId.GenerateNewId();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            await userSetCol.InsertOneAsync(new BsonDocument
            {
                ["_id"] = userSetId.ToString(),
                ["Id"] = userSetId.ToString(),
                ["UserId"] = input.UserId,
                ["StickerSetId"] = setId,
                ["AccessHash"] = setAccessHash,
                ["ShortName"] = setDoc["ShortName"].AsString,
                ["Title"] = setDoc["Title"].AsString,
                ["Archived"] = obj.Archived,
                ["InstalledAt"] = now,
                ["Version"] = 1
            });

            logger.LogInformation("User {UserId} installed sticker set {SetId} ({ShortName})", 
                input.UserId, setId, setDoc["ShortName"].AsString);
        }
        else if (obj.Archived)
        {
            await userSetCol.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("Archived", true));
            logger.LogInformation("User {UserId} archived sticker set {SetId}", input.UserId, setId);
        }

        return new TStickerSetInstallResultSuccess();
    }

    private async Task<BsonDocument?> FindSetBySlugAsync(IMongoCollection<BsonDocument> setCol, string slug)
    {
        if (string.IsNullOrEmpty(slug))
            return null;

        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("Slug", slug)).FirstOrDefaultAsync();
        if (setDoc == null)
        {
            setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("ShortName", slug)).FirstOrDefaultAsync();
        }
        return setDoc;
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }
}
