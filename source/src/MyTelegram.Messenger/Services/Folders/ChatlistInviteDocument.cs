using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// One exported <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder link</a>.
/// </summary>
[BsonIgnoreExtraElements]
public class ChatlistInviteDocument
{
    /// <summary><c>chatlist-invite-{slug}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    /// <summary>The user who exported the link.</summary>
    public long CreatorUserId { get; set; }

    /// <summary>The exporter's own folder id. It means nothing to anybody who imports the link.</summary>
    public int FilterId { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<long> PeerIds { get; set; } = [];

    /// <summary><see cref="PeerType"/> names, parallel to <see cref="PeerIds"/>.</summary>
    public List<string> PeerTypes { get; set; } = [];

    public int CreatedDate { get; set; }

    public bool Revoked { get; set; }

    public static string MakeId(string slug) => $"chatlist-invite-{slug}";

    public List<Peer> ToPeers()
    {
        var peers = new List<Peer>();
        for (var i = 0; i < PeerIds.Count && i < PeerTypes.Count; i++)
        {
            if (Enum.TryParse<PeerType>(PeerTypes[i], out var peerType))
            {
                peers.Add(new Peer(peerType, PeerIds[i]));
            }
        }

        return peers;
    }
}
