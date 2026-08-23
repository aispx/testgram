namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Ownership checks shared by the steps that follow <c>messages.initHistoryImport</c>.
/// See https://corefork.telegram.org/api/import
/// </summary>
internal static class HistoryImportAccess
{
    /// <summary>
    /// Loads an import that must still be collecting media, and belong to this user and this chat.
    /// Everything else is reported as <c>IMPORT_ID_INVALID</c>, so a client cannot probe for the
    /// imports of other accounts.
    /// </summary>
    public static async Task<HistoryImportDocument> LoadPendingAsync(IHistoryImportStore store, long importId,
        long userId, Peer peer)
    {
        var import = await store.GetAsync(importId);
        if (import == null ||
            import.UserId != userId ||
            import.PeerId != peer.PeerId ||
            import.PeerType != peer.PeerType.ToString() ||
            import.Status != HistoryImportStatus.Pending)
        {
            RpcErrors.RpcErrors400.ImportIdInvalid.ThrowRpcError();
        }

        return import!;
    }
}
