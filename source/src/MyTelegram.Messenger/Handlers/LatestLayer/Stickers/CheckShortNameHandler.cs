using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Check whether the given short name is available
/// Possible errors
/// Code Type Description
/// 400 SHORT_NAME_INVALID The specified short name is invalid.
/// 400 SHORT_NAME_OCCUPIED The specified short name is already in use.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.checkShortName"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class CheckShortNameHandler(IStickerSetStore stickerSetStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestCheckShortName, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestCheckShortName obj)
    {
        var shortName = obj.ShortName?.Trim() ?? string.Empty;

        if (!StickerShortNameHelper.IsValid(shortName))
        {
            RpcErrors.RpcErrors400.ShortNameInvalid.ThrowRpcError();
        }

        if (await stickerSetStore.ShortNameExistsAsync(shortName))
        {
            RpcErrors.RpcErrors400.ShortNameOccupied.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
