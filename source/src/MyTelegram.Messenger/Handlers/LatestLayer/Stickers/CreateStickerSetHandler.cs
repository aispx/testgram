using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Create a stickerset, bots only.
/// Possible errors
/// Code Type Description
/// 400 PACK_SHORT_NAME_INVALID Invalid sticker pack name. It must begin with a letter, can't contain consecutive underscores and must end in "_by_&lt;bot username&gt;".
/// 400 PACK_SHORT_NAME_OCCUPIED A stickerpack with this name already exists.
/// 400 PACK_TITLE_INVALID The stickerpack title is invalid.
/// 400 STICKERS_EMPTY No sticker provided.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.createStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class CreateStickerSetHandler(
    IMongoDatabase mongoDatabase,
    IStickerSetStore stickerSetStore,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper,
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerUpdateNotifier updateNotifier,
    IUserAppService userAppService,
    ILogger<CreateStickerSetHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestCreateStickerSet,
        MyTelegram.Schema.Messages.IStickerSet>
{
    private const int TitleMaxLength = 64;

    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestCreateStickerSet obj)
    {
        var title = obj.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > TitleMaxLength)
        {
            RpcErrors.RpcErrors400.PackTitleInvalid.ThrowRpcError();
        }

        var ownerUserId = await ResolveOwnerAsync(input, obj.UserId);
        var shortName = await ResolveShortNameAsync(obj.ShortName, title!);

        var setId = await NextStickerSetIdAsync();
        var thumbDocumentId = obj.Thumb is TInputDocument thumb ? thumb.Id : (long?)null;

        var setDocument = await stickerSetEditor.CreateAsync(ownerUserId, setId, title!, shortName,
            obj.Masks, obj.Emojis, obj.TextColor, [..obj.Stickers.OfType<TInputStickerSetItem>()],
            thumbDocumentId);

        var type = stickerSetStore.GetStickerSetType(setDocument);

        // The creator gets it installed, which is what makes it show up in their panel straight away.
        await installedStickerSetStore.InstallAsync(ownerUserId, setId, type, false);

        logger.LogInformation("Created sticker set {SetId} ({ShortName}) with {Count} stickers for user {UserId}",
            setId, shortName, setDocument.GetInt32("Count"), ownerUserId);

        var result = await stickerSetMapper.BuildFullAsync(input, setDocument);

        await updateNotifier.NotifyNewStickerSetAsync(ownerUserId, result, input.AuthKeyId);
        await updateNotifier.NotifyStickerSetsAsync(ownerUserId, type, input.AuthKeyId);

        return result;
    }

    /// <summary>
    /// Whose set this becomes. The parameter exists for bots, which create sets on behalf of the user who
    /// is talking to them; a user account may only name itself, and the field was previously ignored
    /// altogether, so a bot's sets were recorded as belonging to the bot.
    /// </summary>
    private async Task<long> ResolveOwnerAsync(IRequestInput input, IInputUser? inputUser)
    {
        var targetUserId = inputUser switch
        {
            TInputUserSelf => input.UserId,
            TInputUser user => user.UserId,
            null => input.UserId,
            _ => 0
        };

        if (targetUserId == 0)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (targetUserId == input.UserId)
        {
            return targetUserId;
        }

        var caller = await userAppService.GetAsync(input.UserId);
        if (caller?.Bot != true)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var target = await userAppService.GetAsync(targetUserId);
        if (target == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        return targetUserId;
    }

    /// <summary>
    /// The short name must be free and syntactically valid. An empty one is derived from the title, the
    /// same way <c>stickers.suggestShortName</c> would.
    /// </summary>
    private async Task<string> ResolveShortNameAsync(string? requested, string title)
    {
        var shortName = requested?.Trim() ?? string.Empty;
        if (shortName.Length == 0)
        {
            shortName = await SuggestFreeShortNameAsync(StickerShortNameHelper.FromTitle(title));
        }

        if (!StickerShortNameHelper.IsValid(shortName))
        {
            RpcErrors.RpcErrors400.PackShortNameInvalid.ThrowRpcError();
        }

        if (await stickerSetStore.ShortNameExistsAsync(shortName))
        {
            RpcErrors.RpcErrors400.PackShortNameOccupied.ThrowRpcError();
        }

        return shortName;
    }

    private async Task<string> SuggestFreeShortNameAsync(string baseName)
    {
        if (!await stickerSetStore.ShortNameExistsAsync(baseName))
        {
            return baseName;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = $"{baseName}{Random.Shared.Next(100, 1000)}";
            if (!await stickerSetStore.ShortNameExistsAsync(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    /// <summary>
    /// A sequential id from the shared counter, not a random one: a set id is what clients cache by, and a
    /// collision would silently merge two packs.
    /// </summary>
    private async Task<long> NextStickerSetIdAsync()
    {
        var result = await mongoDatabase.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", "sticker_set_id"),
            Builders<BsonDocument>.Update.Inc("seq", 1L),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        return result.GetInt64("seq");
    }
}
