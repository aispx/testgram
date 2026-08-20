using MyTelegram.Messenger.Helpers;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.GeoLive;

/// <summary>
/// Covers the validation limits, the active/stopped rule and the distance maths behind
/// <a href="https://corefork.telegram.org/api/live-location">live geolocations »</a>.
/// </summary>
public class GeoLiveHelperTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(3600)]
    [InlineData(86400)]
    [InlineData(int.MaxValue)] // "until switched off"
    public void ShouldAcceptValidPeriodsOnSend(int period)
    {
        Should.NotThrow(() => GeoLiveHelper.Validate(Input(period: period), forEdit: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(86401)]
    [InlineData(-1)]
    public void ShouldRejectInvalidPeriodsOnSend(int period)
    {
        ErrorOf(() => GeoLiveHelper.Validate(Input(period: period), forEdit: false)).ShouldBe("MEDIA_INVALID");
    }

    [Fact]
    public void ShouldNotRequireAPeriodOnEdit()
    {
        // A coordinate update only carries the new point; the period stays whatever the original
        // send established. TDLib relaxes the same check with for_edit.
        Should.NotThrow(() => GeoLiveHelper.Validate(Input(period: null), forEdit: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(360)]
    public void ShouldAcceptValidHeadings(int heading)
    {
        Should.NotThrow(() => GeoLiveHelper.Validate(Input(heading: heading), forEdit: false));
    }

    [Theory]
    [InlineData(361)]
    [InlineData(-5)]
    public void ShouldRejectInvalidHeadings(int heading)
    {
        ErrorOf(() => GeoLiveHelper.Validate(Input(heading: heading), forEdit: false)).ShouldBe("MEDIA_INVALID");
    }

    [Fact]
    public void ShouldAcceptAHeadingOfZeroAsUnknown()
    {
        // The Android client sets the heading flag on every periodic update and sends
        // Location.getBearing(), which is 0 whenever the device is stationary or the fix is
        // network/fused. Rejecting that would fail every update and freeze the shared location.
        Should.NotThrow(() => GeoLiveHelper.Validate(Input(heading: 0), forEdit: false));
        Should.NotThrow(() => GeoLiveHelper.Validate(Input(heading: 0), forEdit: true));
    }

    [Fact]
    public void ShouldNormalizeAZeroHeadingToUnknown()
    {
        // 0 is the wire sentinel for "no bearing", not a real direction: TDLib omits the field at 0.
        GeoLiveHelper.NormalizeHeading(0).ShouldBeNull();
        GeoLiveHelper.NormalizeHeading(null).ShouldBeNull();
        GeoLiveHelper.NormalizeHeading(90).ShouldBe(90);
    }

    [Fact]
    public void EditingWithAZeroHeadingShouldKeepThePreviousDirection()
    {
        var old = Media(period: 3600);
        old.Heading = 90;

        var edited = GeoLiveHelper.BuildEditedMedia(
            new TInputMediaGeoLive { GeoPoint = new TInputGeoPoint { Lat = 1, Long = 2 }, Heading = 0 },
            old, startDate: 1000, now: 1300);

        // A stationary update reports heading 0; that is "unknown", so it must not overwrite the
        // last known direction with a bogus one.
        edited.Heading.ShouldBe(90);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100000)]
    public void ShouldAcceptValidProximityRadius(int radius)
    {
        Should.NotThrow(() => GeoLiveHelper.Validate(Input(proximityRadius: radius), forEdit: false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void ShouldRejectInvalidProximityRadius(int radius)
    {
        ErrorOf(() => GeoLiveHelper.Validate(Input(proximityRadius: radius), forEdit: false))
            .ShouldBe("MEDIA_INVALID");
    }

    [Fact]
    public void ShouldTreatALocationInsideItsPeriodAsActive()
    {
        var media = Media(period: 600);

        GeoLiveHelper.IsActive(media, startDate: 1000, now: 1300).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTreatALocationPastItsPeriodAsInactive()
    {
        // date + period is the rule every client applies (TDLib expires_in, telegram-tt
        // isGeoLiveExpired), so the server must agree exactly.
        var media = Media(period: 600);

        GeoLiveHelper.IsActive(media, startDate: 1000, now: 1600).ShouldBeFalse();
    }

    [Fact]
    public void ShouldTreatAnUnlimitedLocationAsAlwaysActive()
    {
        var media = Media(period: int.MaxValue);

        GeoLiveHelper.IsActive(media, startDate: 1000, now: 999_999_999).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTreatAStoppedLocationAsInactiveImmediately()
    {
        var old = Media(period: 3600);

        // Stopped 300s into a 1h share: the window is shortened to what already elapsed.
        var stopped = GeoLiveHelper.BuildEditedMedia(
            new TInputMediaGeoLive { Stopped = true, GeoPoint = new TInputGeoPointEmpty() },
            old, startDate: 1000, now: 1300);

        stopped.Period.ShouldBe(300);
        GeoLiveHelper.IsActive(stopped, startDate: 1000, now: 1300).ShouldBeFalse();
    }

    [Fact]
    public void StoppedPeriodShouldNeverBeZeroOrNegative()
    {
        // A non-positive period makes TDLib log an error and downgrade the message to a plain
        // messageLocation, and makes tdesktop drop the "Live location" title, hiding that this ever
        // was a live location rather than showing it as finished.
        GeoLiveHelper.StoppedPeriod(startDate: 1000, now: 1000).ShouldBe(1);
        GeoLiveHelper.StoppedPeriod(startDate: 1000, now: 900).ShouldBe(1);
    }

    [Fact]
    public void StoppingAnUnlimitedLocationShouldEndIt()
    {
        var old = Media(period: int.MaxValue);

        var stopped = GeoLiveHelper.BuildEditedMedia(
            new TInputMediaGeoLive { Stopped = true, GeoPoint = new TInputGeoPointEmpty() },
            old, startDate: 1000, now: 1100);

        stopped.Period.ShouldBe(100);
        GeoLiveHelper.IsActive(stopped, startDate: 1000, now: 1100).ShouldBeFalse();
    }

    [Fact]
    public void StoppingShouldKeepTheLastKnownPointAndClearLiveOnlyFields()
    {
        var old = Media(period: 3600, lat: 55.75, lon: 37.61);
        old.Heading = 90;
        old.ProximityNotificationRadius = 500;

        // Stopping sends inputGeoPointEmpty: the final position has to survive so clients can still
        // show where sharing ended.
        var stopped = GeoLiveHelper.BuildEditedMedia(
            new TInputMediaGeoLive { Stopped = true, GeoPoint = new TInputGeoPointEmpty() },
            old, startDate: 1000, now: 1300);

        stopped.Heading.ShouldBeNull();
        stopped.ProximityNotificationRadius.ShouldBeNull();
        var point = GeoLiveHelper.GetPoint(stopped);
        point.ShouldNotBeNull();
        point!.Value.Lat.ShouldBe(55.75);
        point.Value.Long.ShouldBe(37.61);
    }

    [Fact]
    public void EditingShouldMoveThePointAndPreserveTheOriginalPeriod()
    {
        var old = Media(period: 3600, lat: 55.75, lon: 37.61);

        var edited = GeoLiveHelper.BuildEditedMedia(
            new TInputMediaGeoLive
            {
                GeoPoint = new TInputGeoPoint { Lat = 55.80, Long = 37.70 },
                Heading = 180
            },
            old, startDate: 1000, now: 1300);

        // The validity window is anchored to the original send, so an update must not extend it.
        edited.Period.ShouldBe(3600);
        edited.Heading.ShouldBe(180);
        GeoLiveHelper.GetPoint(edited)!.Value.Lat.ShouldBe(55.80);
        GeoLiveHelper.IsActive(edited, startDate: 1000, now: 1300).ShouldBeTrue();
    }

    [Fact]
    public void EditingWithoutAProximityRadiusShouldKeepThePreviousOne()
    {
        var old = Media(period: 3600);
        old.ProximityNotificationRadius = 750;

        var edited = GeoLiveHelper.BuildEditedMedia(
            new TInputMediaGeoLive { GeoPoint = new TInputGeoPoint { Lat = 1, Long = 2 } },
            old, startDate: 1000, now: 1300);

        // A coordinate-only update must not silently disarm the member's proximity alert.
        edited.ProximityNotificationRadius.ShouldBe(750);
    }

    [Fact]
    public void ShouldTreatANonPositivePeriodAsInactive()
    {
        // Defensive: legacy rows or a hand-written client could carry period 0.
        GeoLiveHelper.IsActive(Media(period: 0), startDate: 1000, now: 1001).ShouldBeFalse();
    }

    [Fact]
    public void ShouldMeasureDistanceBetweenTwoPoints()
    {
        // Red Square -> Saint Basil's, ~600 m apart; a coarse band keeps the assertion robust while
        // still catching unit or formula mistakes.
        var distance = GeoLiveHelper.DistanceMeters(55.7539, 37.6208, 55.7525, 37.6231);

        distance.ShouldBeInRange(150, 400);
    }

    [Fact]
    public void ShouldMeasureZeroDistanceForTheSamePoint()
    {
        GeoLiveHelper.DistanceMeters(55.7539, 37.6208, 55.7539, 37.6208).ShouldBe(0);
    }

    [Fact]
    public void ShouldMeasureAKnownLongDistance()
    {
        // Moscow -> Saint Petersburg is ~635 km great-circle.
        var distance = GeoLiveHelper.DistanceMeters(55.7558, 37.6173, 59.9311, 30.3609);

        distance.ShouldBeInRange(600_000, 670_000);
    }

    private static string ErrorOf(Action action)
    {
        return Should.Throw<RpcException>(action).RpcError.Message;
    }

    private static TInputMediaGeoLive Input(int? period = 3600, int? heading = null, int? proximityRadius = null)
    {
        return new TInputMediaGeoLive
        {
            GeoPoint = new TInputGeoPoint { Lat = 55.75, Long = 37.61 },
            Period = period,
            Heading = heading,
            ProximityNotificationRadius = proximityRadius
        };
    }

    private static TMessageMediaGeoLive Media(int period, double lat = 55.75, double lon = 37.61)
    {
        return new TMessageMediaGeoLive
        {
            Period = period,
            Geo = new TGeoPoint { Lat = lat, Long = lon, AccessHash = 1 }
        };
    }
}
