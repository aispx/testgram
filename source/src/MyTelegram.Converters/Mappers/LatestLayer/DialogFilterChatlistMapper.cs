namespace MyTelegram.Converters.Mappers.LatestLayer;

/// <summary>
/// A folder imported from a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder
/// deep link</a> is served as <c>dialogFilterChatlist</c>, not as <c>dialogFilter</c>: clients branch on
/// the constructor to decide whether the folder is a shared one (Android keeps
/// <c>MessagesController.DialogFilter.isChatlist</c> from it and only then offers the shared-folder
/// screens), and the constructor has no <c>exclude_peers</c> or type flags at all.
/// </summary>
internal sealed class DialogFilterChatlistMapper
    : IObjectMapper<DialogFilter, TDialogFilterChatlist>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;

    public TDialogFilterChatlist Map(DialogFilter source)
    {
        return Map(source, new TDialogFilterChatlist());
    }

    public TDialogFilterChatlist Map(
        DialogFilter source,
        TDialogFilterChatlist destination
    )
    {
        destination.Id = source.Id;
        destination.Title = source.Title;
        destination.TitleNoanimate = source.TitleNoAnimate;
        destination.Emoticon = source.Emoticon;
        destination.Color = source.Color;

        destination.PinnedPeers = [];
        destination.IncludePeers = [];

        foreach (var peer in source.PinnedPeers)
        {
            destination.PinnedPeers.Add(peer.ToInputPeer());
        }

        foreach (var peer in source.IncludePeers)
        {
            destination.IncludePeers.Add(peer.ToInputPeer());
        }

        return destination;
    }
}
