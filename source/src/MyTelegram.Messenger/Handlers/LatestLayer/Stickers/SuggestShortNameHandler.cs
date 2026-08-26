using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Suggests a short name for a given stickerpack name
/// Possible errors
/// Code Type Description
/// 400 TITLE_INVALID The specified stickerpack title is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.suggestShortName"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SuggestShortNameHandler(IStickerSetStore stickerSetStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestSuggestShortName,
        MyTelegram.Schema.Stickers.ISuggestedShortName>
{
    protected override async Task<MyTelegram.Schema.Stickers.ISuggestedShortName> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Stickers.RequestSuggestShortName obj)
    {
        // A null title used to reach String.Replace and take the request down with a NullReferenceException.
        if (string.IsNullOrWhiteSpace(obj.Title))
        {
            RpcErrors.RpcErrors400.TitleInvalid.ThrowRpcError();
        }

        var baseName = StickerShortNameHelper.FromTitle(obj.Title);

        // The client puts the suggestion straight into createStickerSet, so handing back a name that is
        // already taken turns into PACK_SHORT_NAME_OCCUPIED one call later.
        var shortName = baseName;
        for (var attempt = 0; attempt < 10 && await stickerSetStore.ShortNameExistsAsync(shortName); attempt++)
        {
            shortName = $"{baseName}{Random.Shared.Next(100, 1000)}";
        }

        return new MyTelegram.Schema.Stickers.TSuggestedShortName { ShortName = shortName };
    }
}
