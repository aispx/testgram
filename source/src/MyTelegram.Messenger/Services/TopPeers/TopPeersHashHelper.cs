namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>
/// The <c>hash</c> of <c>contacts.getTopPeers</c>.
/// </summary>
/// <remarks>
/// <para>This hash is the <b>client's</b> computation, quoted back, so the algorithm is not ours to
/// choose. Two client families send a real one and both use the
/// <a href="https://corefork.telegram.org/api/offsets#hash-generation">documented unsigned
/// accumulator</a>: tdlib folds <c>get_vector_hash</c> over the bare peer ids of every category it has
/// cached, concatenated in <c>TopDialogCategory</c> enum order
/// (<c>TopDialogManager::do_get_top_peers</c>), and tdesktop folds
/// <c>HashInit/HashUpdate/HashFinalize</c> over <c>peerToUser(...).bare</c> of the single category it
/// asked for, capped at 64 (<c>Data::TopPeers::countHash</c>). Android, iOS, macOS, tweb and
/// telegram-tt all send <c>0</c>.</para>
/// <para>So the server has to fold the ids it is about to send, in the order it is about to send them,
/// and with an unsigned accumulator — <c>MessageSearchMongoHelper.CalcHash</c> shifts a signed
/// <c>long</c> and disagrees with every client the moment the accumulator goes negative, which makes
/// <c>contacts.topPeersNotModified</c> unreachable and the whole list a re-download on every poll.</para>
/// <para>The id is the bare one: a channel folds in as its <c>channel_id</c>, not as a marked dialog
/// id, which is what both reference implementations push.</para>
/// </remarks>
public static class TopPeersHashHelper
{
    public static long ComputeHash(IEnumerable<ITopPeerCategoryPeers> categories)
    {
        return VectorHashHelper.ComputeHash(categories
            .SelectMany(p => p.Peers)
            .Select(p => p.Peer.ToPeerId() ?? 0));
    }
}
