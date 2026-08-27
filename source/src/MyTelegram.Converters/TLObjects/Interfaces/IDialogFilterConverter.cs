namespace MyTelegram.Converters.TLObjects.Interfaces;

public interface IDialogFilterConverter : ILayeredConverter
{
    /// <param name="dialogFilter">The stored folder.</param>
    /// <param name="hasMyInvites">
    /// Whether the caller has exported at least one <a
    /// href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder link</a> for this
    /// folder. Only meaningful for a shareable folder, where it becomes
    /// <c>dialogFilterChatlist.has_my_invites</c>.
    /// </param>
    IDialogFilter ToDialogFilter(DialogFilter dialogFilter, bool hasMyInvites = false);
}