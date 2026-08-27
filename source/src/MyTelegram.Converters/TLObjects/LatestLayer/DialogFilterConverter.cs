namespace MyTelegram.Converters.TLObjects.LatestLayer;

internal sealed class DialogFilterConverter(IObjectMapper objectMapper) : IDialogFilterConverter, ITransientDependency
{
    public int Layer => Layers.LayerLatest;

    public IDialogFilter ToDialogFilter(DialogFilter dialogFilter, bool hasMyInvites = false)
    {
        if (dialogFilter.IsChatlist)
        {
            var chatlist = objectMapper.Map<DialogFilter, TDialogFilterChatlist>(dialogFilter);
            chatlist.HasMyInvites = hasMyInvites;

            return chatlist;
        }

        return objectMapper.Map<DialogFilter, TDialogFilter>(dialogFilter);
    }
}