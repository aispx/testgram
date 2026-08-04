namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Get the set of <a href="https://corefork.telegram.org/api/colors">accent color palettes »</a> that can be used in profile page backgrounds.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getPeerProfileColors"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPeerProfileColorsHandler(IPeerColorPaletteProvider peerColorPaletteProvider)
    : RpcResultObjectHandler<RequestGetPeerProfileColors, IPeerColors>
{
    protected override Task<IPeerColors> HandleCoreAsync(IRequestInput input, RequestGetPeerProfileColors obj)
    {
        var options = peerColorPaletteProvider.GetProfileColorOptions();
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
