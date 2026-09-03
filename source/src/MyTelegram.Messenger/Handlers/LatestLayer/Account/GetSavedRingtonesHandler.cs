using MyTelegram.Messenger.Services.Documents;
using MyTelegram.Messenger.Services.Ringtones;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Fetch saved notification sounds
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getSavedRingtones"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The hash is the <b>server's</b> to define: every client stores the value from the response and
/// quotes it back unchanged (Android keeps it in preferences, tdesktop in <c>_list.hash</c>, iOS in the
/// cached sound list, tdlib in its log event) and none of them computes one. This used to answer
/// <c>Hash = obj.Hash</c> — an echo of whatever the client sent — so <c>savedRingtonesNotModified</c>
/// could never be reached and the whole list was re-sent on every poll, which nothing in the logs
/// distinguishes from healthy.</para>
///
/// <para>The order is a contract too: clients render the vector as received, and a fresh sound belongs at
/// the front. Loading the documents with one <c>$in</c> and iterating <i>that</i> result, as this used to,
/// leaves the order up to Mongo — so the list changed between two calls with the same content.</para>
/// </remarks>
internal sealed class GetSavedRingtonesHandler(
    ISavedRingtoneStore savedRingtoneStore,
    IRingtoneLimits limits,
    IDocumentReader documentReader)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetSavedRingtones,
        MyTelegram.Schema.Account.ISavedRingtones>
{
    protected override async Task<MyTelegram.Schema.Account.ISavedRingtones> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetSavedRingtones obj)
    {
        var savedRows = await savedRingtoneStore.GetOrderedAsync(input.UserId, limits.MaxSavedCount);
        var documents = await documentReader.GetAsync(savedRows.ConvertAll(p => p.DocumentId));

        var ringtones = new TVector<MyTelegram.Schema.IDocument>();
        var orderedIds = new List<long>(savedRows.Count);
        var staleIds = new List<long>();

        foreach (var row in savedRows)
        {
            // A sound whose document is gone cannot be served, and leaving the row behind would keep it
            // in the count forever while never appearing in the list.
            if (!documents.TryGetValue(row.DocumentId, out var document))
            {
                staleIds.Add(row.DocumentId);
                continue;
            }

            // The duration was probed when the sound was uploaded; the file server's row does not carry it,
            // and a tone with no documentAttributeAudio shows no length in any client.
            ringtones.Add(RingtoneAudioAttribute.Merge(documentReader.Map(document), row));
            orderedIds.Add(row.DocumentId);
        }

        if (staleIds.Count > 0)
        {
            await savedRingtoneStore.RemoveManyAsync(input.UserId, staleIds);
        }

        var hash = SavedRingtoneHashHelper.ComputeHash(orderedIds);

        // A zero hash is what a client sends when it has nothing cached, so it can never be up to date.
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Account.TSavedRingtonesNotModified();
        }

        return new MyTelegram.Schema.Account.TSavedRingtones
        {
            Hash = hash,
            Ringtones = ringtones
        };
    }
}
