using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get info about a stickerset.
/// Possible errors
/// Code Type Description
/// 400 EMOTICON_STICKERPACK_MISSING inputStickerSetDice.emoji cannot be empty.
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetStickerSetHandler(IStickerSetStore stickerSetStore, IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetStickerSet, MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetStickerSet obj)
    {
        // An empty dice emoji has its own documented error, distinct from an unknown one:
        // see https://corefork.telegram.org/api/dice
        if (obj.Stickerset is TInputStickerSetDice { Emoticon: null or "" })
        {
            RpcErrors.RpcErrors400.EmoticonStickerpackMissing.ThrowRpcError();
        }

        var lookup = await stickerSetStore.FindAsync(obj.Stickerset);
        if (lookup.Set == null)
        {
            // A synthetic empty set with access_hash = 0 used to go out here, which clients cache as a
            // real pack and then draw as an empty tab forever.
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var result = await stickerSetMapper.BuildFullAsync(input, lookup.Set!, lookup.Emoticon);

        // A zero request hash means the client has nothing cached, so it can never be satisfied by
        // notModified even if our hash happened to be zero — which it never is.
        if (obj.Hash != 0 && obj.Hash == result.Set.Hash)
        {
            return new MyTelegram.Schema.Messages.TStickerSetNotModified();
        }

        return result;
    }
}
