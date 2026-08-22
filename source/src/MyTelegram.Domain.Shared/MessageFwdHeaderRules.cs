namespace MyTelegram;

/// <summary>
/// Shaping rules of a forward header that both forward paths have to agree on.
/// </summary>
public static class MessageFwdHeaderRules
{
    /// <summary>
    /// Carries the origin of a message that was imported from a foreign chat app into the forward
    /// header of a copy of it. The original author has no account — the name is all the import kept —
    /// so the copy becomes a forward from a hidden sender that keeps the original name and date, and
    /// names the account that authored the import in <c>saved_from_id</c>.
    /// The <c>imported</c> flag itself is not carried over: it belongs to the imported message, and a
    /// client that sees it stops drawing the forward header altogether.
    /// See https://corefork.telegram.org/api/import
    /// </summary>
    /// <param name="forwardHeader">Header being built for the copy.</param>
    /// <param name="sourceHeader">Header of the message being forwarded.</param>
    /// <param name="sourceSenderPeer">Sender of the message being forwarded.</param>
    /// <returns>False when the source was not an imported message and the caller's own rules apply.</returns>
    public static bool TryApplyImportedOrigin(MessageFwdHeader forwardHeader, MessageFwdHeader? sourceHeader,
        Peer sourceSenderPeer)
    {
        if (sourceHeader is not { Imported: true })
        {
            return false;
        }

        forwardHeader.Imported = false;
        forwardHeader.FromId = null;
        forwardHeader.FromName = sourceHeader.FromName;
        forwardHeader.Date = sourceHeader.Date;
        forwardHeader.SavedFromId = sourceSenderPeer;

        return true;
    }
}
