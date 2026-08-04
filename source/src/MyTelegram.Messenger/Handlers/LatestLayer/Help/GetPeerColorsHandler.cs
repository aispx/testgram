namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Get the set of <a href="https://corefork.telegram.org/api/colors">accent color palettes »</a> that can be used for message accents.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getPeerColors"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPeerColorsHandler(IPeerColorPaletteProvider peerColorPaletteProvider)
    : RpcResultObjectHandler<RequestGetPeerColors, IPeerColors>
{
    protected override Task<IPeerColors> HandleCoreAsync(IRequestInput input, RequestGetPeerColors obj)
    {
        var options = peerColorPaletteProvider.GetMessageColorOptions();
        var hash = peerColorPaletteProvider.ComputeHash(options);

        if (obj.Hash == hash)
        {
            return Task.FromResult<IPeerColors>(new TPeerColorsNotModified());
        }

        return Task.FromResult<IPeerColors>(new TPeerColors
        {
            Hash = hash,
            Colors = new TVector<IPeerColorOption>(options)
        });
    }
}
