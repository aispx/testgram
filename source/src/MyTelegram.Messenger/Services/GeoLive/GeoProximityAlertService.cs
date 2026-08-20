using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.GeoLive;

/// <summary>
/// Remembers whether one chat member is currently inside another member's proximity radius, so a
/// single approach produces exactly one service message instead of one per location update.
/// See https://corefork.telegram.org/api/live-location#proximity-alert
/// </summary>
public class GeoProximityStateDocument
{
    /// <summary>
    /// <c>{peerType}-{peerId}-{toUserId}-{fromUserId}</c>: one row per watcher/mover pair in a chat.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    /// <summary>True while <c>fromUserId</c> is known to be within <c>toUserId</c>'s radius.</summary>
    public bool Inside { get; set; }

    public int Date { get; set; }
}

public interface IGeoProximityAlertService
{
    /// <summary>
    /// Called after a member's live location moved. Emits
    /// <a href="https://corefork.telegram.org/constructor/messageActionGeoProximityReached">messageActionGeoProximityReached</a>
    /// for every other member of the chat who armed a proximity radius that
    /// <paramref name="movedUserId"/> has just entered.
    /// </summary>
    Task CheckAsync(IRequestInput input, Peer peer, long ownerPeerId, long movedUserId,
        TMessageMediaGeoLive movedMedia);
}

/// <inheritdoc />
public class GeoProximityAlertService(
    IMongoDatabase database,
    IQueryProcessor queryProcessor,
    IMessageAppService messageAppService,
    ILogger<GeoProximityAlertService> logger)
    : IGeoProximityAlertService, ITransientDependency
{
    private const string CollectionName = "geo_proximity_alerts";

    /// <summary>Upper bound on the live locations inspected per update; a chat cannot usefully exceed this.</summary>
    private const int MaxLiveLocations = 100;

    private IMongoCollection<GeoProximityStateDocument> Collection =>
        database.GetCollection<GeoProximityStateDocument>(CollectionName);

    public async Task CheckAsync(IRequestInput input, Peer peer, long ownerPeerId, long movedUserId,
        TMessageMediaGeoLive movedMedia)
    {
        // Broadcast channels have no notion of members sharing locations with each other.
        if (peer.PeerType == PeerType.Channel && !await IsMegagroupAsync(peer.PeerId))
        {
            return;
        }

        if (GeoLiveHelper.GetPoint(movedMedia) is not { } movedPoint)
        {
            return;
        }

        var now = DateTime.UtcNow.ToTimestamp();

        // Every live location visible in this dialog: for a supergroup that is the channel box, for a
        // private or basic chat the caller's own box, which holds both their outbox copy and the
        // inbox copies of the other members. GeoLiveOnly keeps static locations and venues, which
        // share MessageType.Geo, from consuming the window.
        var messages = await queryProcessor.ProcessAsync(new GetMessagesQuery(
            ownerPeerId,
            MessageType.Geo,
            null,
            [],
            0,
            MaxLiveLocations,
            null,
            peer,
            movedUserId,
            0,
            GeoLiveOnly: true));

        foreach (var watcher in messages)
        {
            if (watcher.SenderUserId == movedUserId || watcher.SenderUserId <= 0)
            {
                continue;
            }

            if (watcher.Media2 is not TMessageMediaGeoLive watcherMedia ||
                !GeoLiveHelper.IsActive(watcherMedia, watcher.Date, now))
            {
                continue;
            }

            // Only the member who armed an alert gets notified, and only about their own radius.
            var radius = watcherMedia.ProximityNotificationRadius ?? 0;
            if (radius <= 0)
            {
                continue;
            }

            if (GeoLiveHelper.GetPoint(watcherMedia) is not { } watcherPoint)
            {
                continue;
            }

            var distance = GeoLiveHelper.DistanceMeters(movedPoint.Lat, movedPoint.Long,
                watcherPoint.Lat, watcherPoint.Long);

            await ApplyTransitionAsync(input, peer, watcher.SenderUserId, movedUserId, distance, radius, now);
        }
    }

    /// <summary>
    /// Fires an alert on entering the radius and re-arms it on leaving, so a member loitering near the
    /// boundary does not generate a service message per update.
    /// </summary>
    private async Task ApplyTransitionAsync(IRequestInput input, Peer peer, long watcherUserId, long movedUserId,
        int distance, int radius, int now)
    {
        var id = $"{(int)peer.PeerType}-{peer.PeerId}-{watcherUserId}-{movedUserId}";
        var isInside = distance <= radius;

        // Claim the transition atomically and read the state it replaced. A plain find-then-write would
        // let two concurrent updates for the same pair (a second session, or a retry) both observe
        // "outside" and both send the service message.
        var previous = await Collection.FindOneAndUpdateAsync(
            Builders<GeoProximityStateDocument>.Filter.Eq(p => p.Id, id),
            Builders<GeoProximityStateDocument>.Update
                .Set(p => p.Inside, isInside)
                .Set(p => p.Date, now),
            new FindOneAndUpdateOptions<GeoProximityStateDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.Before
            });

        var wasInside = previous?.Inside ?? false;
        if (wasInside == isInside)
        {
            return;
        }

        if (!isInside)
        {
            // Left the radius: no message, the pair is simply eligible to alert again later.
            return;
        }

        await SendProximityReachedAsync(input, peer, watcherUserId, movedUserId, distance);
    }

    private async Task SendProximityReachedAsync(IRequestInput input, Peer peer, long watcherUserId, long movedUserId,
        int distance)
    {
        // to_id armed the alert, from_id is the member that just came into range.
        var action = new TMessageActionGeoProximityReached
        {
            FromId = new TPeerUser { UserId = movedUserId },
            ToId = new TPeerUser { UserId = watcherUserId },
            Distance = distance
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            movedUserId,
            peer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action);

        try
        {
            await messageAppService.SendMessageAsync([sendInput]);
        }
        catch (Exception ex)
        {
            // A failed alert must not fail the location update that triggered it.
            logger.LogError(ex,
                "Failed to send proximity alert in {PeerType}:{PeerId} for watcher {WatcherUserId}",
                peer.PeerType, peer.PeerId, watcherUserId);
        }
    }

    private async Task<bool> IsMegagroupAsync(long channelId)
    {
        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channelId));
        return channel?.MegaGroup ?? false;
    }
}
