using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — media areas.
///
/// <para>
/// Media areas are stored flattened into a single <see cref="StoryMediaArea"/> shape and rebuilt into
/// their TL constructors on read, so every area type has to survive the round trip with its coordinates
/// and payload intact. A dropped field here shows up client-side as a venue tag pinned to the wrong
/// place, or a reaction bubble that lost its emoji.
/// </para>
/// </summary>
public class StoryMediaAreaTests
{
    [Fact]
    public void Round_trips_a_venue()
    {
        var area = RoundTrip(new TMediaAreaVenue
        {
            Coordinates = Coordinates(),
            Geo = new TGeoPoint { Lat = 55.75, Long = 37.62, AccessHash = 123, AccuracyRadius = 50 },
            Title = "Red Square",
            Address = "Moscow",
            Provider = "foursquare",
            VenueId = "venue-1",
            VenueType = "landmark"
        });

        var venue = area.ShouldBeOfType<TMediaAreaVenue>();
        venue.Title.ShouldBe("Red Square");
        venue.Address.ShouldBe("Moscow");
        venue.Provider.ShouldBe("foursquare");
        venue.VenueId.ShouldBe("venue-1");
        venue.VenueType.ShouldBe("landmark");

        var geo = venue.Geo.ShouldBeOfType<TGeoPoint>();
        geo.Lat.ShouldBe(55.75);
        geo.Long.ShouldBe(37.62);
        geo.AccessHash.ShouldBe(123);
        geo.AccuracyRadius.ShouldBe(50);

        AssertCoordinates(venue.Coordinates);
    }

    [Fact]
    public void Round_trips_an_input_venue()
    {
        var area = RoundTrip(new TInputMediaAreaVenue
        {
            Coordinates = Coordinates(),
            QueryId = 9876543210,
            ResultId = "result-1"
        });

        var venue = area.ShouldBeOfType<TInputMediaAreaVenue>();
        venue.QueryId.ShouldBe(9876543210);
        venue.ResultId.ShouldBe("result-1");
        AssertCoordinates(venue.Coordinates);
    }

    [Fact]
    public void Round_trips_a_geo_point_with_its_address()
    {
        var area = RoundTrip(new TMediaAreaGeoPoint
        {
            Coordinates = Coordinates(),
            Geo = new TGeoPoint { Lat = 1.5, Long = 2.5 },
            Address = new TGeoPointAddress
            {
                CountryIso2 = "RU",
                State = "Moscow",
                City = "Moscow",
                Street = "Tverskaya"
            }
        });

        var geoPoint = area.ShouldBeOfType<TMediaAreaGeoPoint>();
        geoPoint.Geo.ShouldBeOfType<TGeoPoint>().Lat.ShouldBe(1.5);

        var address = geoPoint.Address.ShouldBeOfType<TGeoPointAddress>();
        address.CountryIso2.ShouldBe("RU");
        address.State.ShouldBe("Moscow");
        address.City.ShouldBe("Moscow");
        address.Street.ShouldBe("Tverskaya");
    }

    [Fact]
    public void Round_trips_a_suggested_reaction_with_an_emoji()
    {
        var area = RoundTrip(new TMediaAreaSuggestedReaction
        {
            Coordinates = Coordinates(),
            Dark = true,
            Flipped = true,
            Reaction = new TReactionEmoji { Emoticon = "👍" }
        });

        var reaction = area.ShouldBeOfType<TMediaAreaSuggestedReaction>();
        reaction.Dark.ShouldBeTrue();
        reaction.Flipped.ShouldBeTrue();
        reaction.Reaction.ShouldBeOfType<TReactionEmoji>().Emoticon.ShouldBe("👍");
    }

    [Fact]
    public void Round_trips_a_suggested_reaction_with_a_custom_emoji()
    {
        var area = RoundTrip(new TMediaAreaSuggestedReaction
        {
            Coordinates = Coordinates(),
            Reaction = new TReactionCustomEmoji { DocumentId = 555 }
        });

        area.ShouldBeOfType<TMediaAreaSuggestedReaction>()
            .Reaction.ShouldBeOfType<TReactionCustomEmoji>()
            .DocumentId.ShouldBe(555);
    }

    [Fact]
    public void Round_trips_a_channel_post()
    {
        var area = RoundTrip(new TMediaAreaChannelPost
        {
            Coordinates = Coordinates(),
            ChannelId = 4242,
            MsgId = 77
        });

        var post = area.ShouldBeOfType<TMediaAreaChannelPost>();
        post.ChannelId.ShouldBe(4242);
        post.MsgId.ShouldBe(77);
    }

    [Fact]
    public void An_input_channel_post_comes_back_resolved()
    {
        // The client only understands the resolved form here, and channel_id is all the input
        // constructor carried anyway.
        var area = RoundTrip(new TInputMediaAreaChannelPost
        {
            Coordinates = Coordinates(),
            Channel = new TInputChannel { ChannelId = 4242, AccessHash = 1 },
            MsgId = 77
        });

        var post = area.ShouldBeOfType<TMediaAreaChannelPost>();
        post.ChannelId.ShouldBe(4242);
        post.MsgId.ShouldBe(77);
    }

    [Fact]
    public void Round_trips_a_url()
    {
        RoundTrip(new TMediaAreaUrl { Coordinates = Coordinates(), Url = "https://t.me/x" })
            .ShouldBeOfType<TMediaAreaUrl>()
            .Url.ShouldBe("https://t.me/x");
    }

    [Fact]
    public void Round_trips_weather()
    {
        var weather = RoundTrip(new TMediaAreaWeather
        {
            Coordinates = Coordinates(),
            Emoji = "☀️",
            TemperatureC = 21.5,
            Color = 0xFFAA00
        }).ShouldBeOfType<TMediaAreaWeather>();

        weather.Emoji.ShouldBe("☀️");
        weather.TemperatureC.ShouldBe(21.5);
        weather.Color.ShouldBe(0xFFAA00);
    }

    [Fact]
    public void Round_trips_a_star_gift()
    {
        RoundTrip(new TMediaAreaStarGift { Coordinates = Coordinates(), Slug = "gift-slug" })
            .ShouldBeOfType<TMediaAreaStarGift>()
            .Slug.ShouldBe("gift-slug");
    }

    [Fact]
    public void Parsing_a_list_keeps_every_area()
    {
        var stored = StoryMediaAreaHelper.Parse(
        [
            new TMediaAreaUrl { Coordinates = Coordinates(), Url = "https://a" },
            new TMediaAreaStarGift { Coordinates = Coordinates(), Slug = "b" }
        ]);

        stored.Count.ShouldBe(2);
        StoryMediaAreaHelper.ToMediaAreas(stored)!.Count.ShouldBe(2);
    }

    [Fact]
    public void An_empty_area_list_becomes_null_rather_than_an_empty_vector()
    {
        // storyItem.media_areas is an optional field: an empty vector would set the flag for nothing.
        StoryMediaAreaHelper.Parse(null).ShouldBeEmpty();
        StoryMediaAreaHelper.ToMediaAreas([]).ShouldBeNull();
        StoryMediaAreaHelper.ToMediaAreas(null).ShouldBeNull();
    }

    [Fact]
    public void Geo_matching_respects_the_search_radius()
    {
        var area = StoryMediaAreaHelper.ParseOne(new TMediaAreaGeoPoint
        {
            Coordinates = Coordinates(),
            Geo = new TGeoPoint { Lat = 10, Long = 20 }
        })!;

        StoryMediaAreaHelper.MatchesGeo(area, 10.1, 20.1, 0.5).ShouldBeTrue();
        StoryMediaAreaHelper.MatchesGeo(area, 12, 20, 0.5).ShouldBeFalse();
    }

    [Fact]
    public void An_area_without_coordinates_never_matches_a_geo_search()
    {
        var area = StoryMediaAreaHelper.ParseOne(
            new TMediaAreaUrl { Coordinates = Coordinates(), Url = "https://a" })!;

        StoryMediaAreaHelper.MatchesGeo(area, 10, 20, 0.5).ShouldBeFalse();
    }

    private static IMediaAreaCoordinates Coordinates() => new TMediaAreaCoordinates
    {
        X = 50.5,
        Y = 60.5,
        W = 20,
        H = 10,
        Rotation = 15,
        Radius = 5
    };

    private static void AssertCoordinates(IMediaAreaCoordinates? coordinates)
    {
        var c = coordinates.ShouldBeOfType<TMediaAreaCoordinates>();
        c.X.ShouldBe(50.5);
        c.Y.ShouldBe(60.5);
        c.W.ShouldBe(20);
        c.H.ShouldBe(10);
        c.Rotation.ShouldBe(15);
        c.Radius.ShouldBe(5);
    }

    private static IMediaArea RoundTrip(IMediaArea area)
    {
        var stored = StoryMediaAreaHelper.ParseOne(area);
        stored.ShouldNotBeNull();

        var converted = StoryMediaAreaHelper.ToMediaArea(stored);
        converted.ShouldNotBeNull();

        return converted;
    }
}
