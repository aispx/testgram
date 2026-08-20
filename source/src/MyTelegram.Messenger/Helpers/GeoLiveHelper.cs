namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Shared validation and geometry for <a href="https://corefork.telegram.org/api/live-location">live
/// geolocations »</a>, used by <c>messages.sendMedia</c>, <c>messages.editMessage</c>, the geo-live
/// expiration background service and the proximity-alert logic so the limits and the active/expired
/// rule are enforced identically everywhere.
/// </summary>
internal static class GeoLiveHelper
{
    /// <summary>
    /// A <c>period</c> of <see cref="int.MaxValue"/> (<c>0x7FFFFFFF</c>) means the location is shared
    /// until switched off manually — an "infinite" live location that never expires on its own.
    /// See TDLib <c>LocationController</c> and telegram clients (period == INT_MAX ⇒ no stop time).
    /// </summary>
    public const int InfinitePeriod = int.MaxValue;

    // Server-side limits, mirrored from TDLib Location.cpp (process_live_location).
    public const int MinPeriod = 60;        // seconds
    public const int MaxPeriod = 86400;     // seconds
    public const int MinHeading = 1;        // degrees
    public const int MaxHeading = 360;      // degrees
    public const int MaxProximityRadius = 100000; // meters

    private const double EarthRadiusMeters = 6371000d;

    /// <summary>
    /// Validates the <c>period</c>/<c>heading</c>/<c>proximity_notification_radius</c> of an
    /// <c>inputMediaGeoLive</c>. <paramref name="forEdit"/> relaxes the period check the same way
    /// TDLib does: a subsequent edit only carries updated coordinates and need not repeat the period.
    /// </summary>
    public static void Validate(TInputMediaGeoLive input, bool forEdit)
    {
        if (!forEdit)
        {
            var period = input.Period ?? 0;
            if (period != InfinitePeriod && (period < MinPeriod || period > MaxPeriod))
            {
                RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
            }
        }

        if (NormalizeHeading(input.Heading) is { } heading && (heading < MinHeading || heading > MaxHeading))
        {
            RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
        }

        if (input.ProximityNotificationRadius is { } radius && (radius < 0 || radius > MaxProximityRadius))
        {
            RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
        }
    }

    /// <summary>
    /// Maps a heading of <c>0</c> to "unknown". <c>0</c> is the on-the-wire sentinel for a fix without a
    /// bearing, not an out-of-range value: TDLib omits the field entirely at 0
    /// (<c>if (heading != 0) flags |= HEADING_MASK</c>), while the Android client sets the heading flag
    /// on every periodic update and sends <c>Location.getBearing()</c>, which is <c>0</c> whenever the
    /// device is stationary or the fix is network/fused. Rejecting it would make every update from such
    /// a device fail and freeze the shared location.
    /// </summary>
    public static int? NormalizeHeading(int? heading) => heading is null or 0 ? null : heading;

    /// <summary>
    /// A live location is active while its validity period has not elapsed. The receiving
    /// <c>messageMediaGeoLive</c> carries no explicit "stopped" flag, so every client derives this
    /// from <c>message.date + period</c> vs. now — matching TDLib (<c>expires_in</c>) and telegram-tt
    /// (<c>isGeoLiveExpired</c>). Stopping early is therefore expressed by shortening the stored
    /// period; see <see cref="BuildEditedMedia"/>.
    /// </summary>
    public static bool IsActive(TMessageMediaGeoLive media, int startDate, int now)
    {
        // Guard the sentinel separately: startDate + int.MaxValue would overflow.
        if (media.Period == InfinitePeriod)
        {
            return true;
        }

        return media.Period > 0 && startDate + media.Period > now;
    }

    /// <summary>
    /// Great-circle distance in meters between two WGS-84 points (haversine). Used for proximity
    /// alerts; accuracy at chat-relevant distances is well within the meter granularity the alert uses.
    /// </summary>
    public static int DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (int)Math.Round(EarthRadiusMeters * c);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    /// <summary>Extracts (lat, long) from a stored geo-live media, or null if the point is empty.</summary>
    public static (double Lat, double Long)? GetPoint(TMessageMediaGeoLive media)
    {
        return media.Geo is TGeoPoint p ? (p.Lat, p.Long) : null;
    }

    /// <summary>
    /// Builds the stored <c>messageMediaGeoLive</c> for an edit of an existing live location.
    /// The original validity <c>period</c> is preserved (an edit only carries updated coordinates,
    /// per TDLib <c>process_live_location(for_edit=true)</c>); stopping shortens it to the time
    /// already elapsed while keeping the last reported point, so clients still show where sharing
    /// ended. See https://corefork.telegram.org/api/live-location
    /// </summary>
    /// <param name="startDate">Date of the original message, which anchors the validity window.</param>
    /// <param name="now">Current server time, used to shorten the period when stopping.</param>
    public static TMessageMediaGeoLive BuildEditedMedia(TInputMediaGeoLive input, TMessageMediaGeoLive old,
        int startDate, int now)
    {
        // A normal update carries the new point; stopping sends inputGeoPointEmpty, so the last
        // known point is kept.
        IGeoPoint geo = input.GeoPoint is TInputGeoPoint p
            ? new TGeoPoint
            {
                AccuracyRadius = p.AccuracyRadius,
                Lat = p.Lat,
                Long = p.Long,
                AccessHash = Random.Shared.NextInt64()
            }
            : old.Geo;

        return new TMessageMediaGeoLive
        {
            Geo = geo,
            // Once stopped the direction of movement is meaningless, mirroring how clients drop
            // heading the moment a live location expires.
            Heading = input.Stopped ? null : NormalizeHeading(input.Heading) ?? old.Heading,
            Period = input.Stopped ? StoppedPeriod(startDate, now) : old.Period,
            // Clients resend the proximity radius on every update; keep the previous value when the
            // field is omitted so a coordinate-only edit does not silently disable alerts.
            ProximityNotificationRadius =
                input.Stopped ? null : input.ProximityNotificationRadius ?? old.ProximityNotificationRadius
        };
    }

    /// <summary>
    /// The period that marks a live location as no longer being shared: the time already elapsed, so
    /// that <c>startDate + period</c> is no longer in the future and <see cref="IsActive"/> is false.
    /// </summary>
    /// <remarks>
    /// Deliberately never <c>0</c> or negative. A non-positive period makes TDLib log
    /// <c>"Receive wrong live location period"</c> and downgrade the message to a plain
    /// <c>messageLocation</c>, and makes tdesktop drop the "Live location" title — which would hide
    /// the fact that this ever was a live location instead of showing it as finished. The floor of 1
    /// leaves a location stopped within the same second as it was sent nominally valid for that one
    /// second, which no client renders differently.
    /// </remarks>
    public static int StoppedPeriod(int startDate, int now) => Math.Max(1, now - startDate);
}
