using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Converts <a href="https://corefork.telegram.org/api/stories#media-areas">media areas</a> between the
/// TL representation and the flat <see cref="StoryMediaArea"/> stored on a story.
/// <para>
/// Input constructors (<c>inputMediaAreaVenue</c>, <c>inputMediaAreaChannelPost</c>) are stored as-is
/// rather than being resolved: resolving a venue needs the inline-bot result that produced
/// <c>query_id</c>/<c>result_id</c>, which is not available server-side after the fact. They round-trip
/// back to the client as the same input constructors, which official clients accept.
/// </para>
/// </summary>
public static class StoryMediaAreaHelper
{
    public static List<StoryMediaArea> Parse(IEnumerable<IMediaArea>? areas)
    {
        var result = new List<StoryMediaArea>();
        if (areas == null)
        {
            return result;
        }

        foreach (var area in areas)
        {
            var parsed = ParseOne(area);
            if (parsed != null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    public static StoryMediaArea? ParseOne(IMediaArea? area)
    {
        switch (area)
        {
            case TMediaAreaVenue venue:
                {
                    var stored = FromCoordinates(venue.Coordinates, StoryMediaAreaType.Venue);
                    ApplyGeo(stored, venue.Geo);
                    stored.Title = venue.Title;
                    stored.Address = venue.Address;
                    stored.Provider = venue.Provider;
                    stored.VenueId = venue.VenueId;
                    stored.VenueType = venue.VenueType;
                    return stored;
                }

            case TInputMediaAreaVenue inputVenue:
                {
                    var stored = FromCoordinates(inputVenue.Coordinates, StoryMediaAreaType.InputVenue);
                    stored.QueryId = inputVenue.QueryId;
                    stored.ResultId = inputVenue.ResultId;
                    return stored;
                }

            case TMediaAreaGeoPoint geoPoint:
                {
                    var stored = FromCoordinates(geoPoint.Coordinates, StoryMediaAreaType.GeoPoint);
                    ApplyGeo(stored, geoPoint.Geo);
                    if (geoPoint.Address is TGeoPointAddress address)
                    {
                        stored.GeoCountryIso2 = address.CountryIso2;
                        stored.GeoState = address.State;
                        stored.GeoCity = address.City;
                        stored.GeoStreet = address.Street;
                    }
                    return stored;
                }

            case TMediaAreaSuggestedReaction suggestedReaction:
                {
                    var stored = FromCoordinates(suggestedReaction.Coordinates, StoryMediaAreaType.SuggestedReaction);
                    stored.Dark = suggestedReaction.Dark;
                    stored.Flipped = suggestedReaction.Flipped;
                    switch (suggestedReaction.Reaction)
                    {
                        case TReactionEmoji emoji:
                            stored.ReactionEmoticon = emoji.Emoticon;
                            break;
                        case TReactionCustomEmoji customEmoji:
                            stored.ReactionDocumentId = customEmoji.DocumentId;
                            break;
                    }
                    return stored;
                }

            case TMediaAreaChannelPost channelPost:
                {
                    var stored = FromCoordinates(channelPost.Coordinates, StoryMediaAreaType.ChannelPost);
                    stored.ChannelId = channelPost.ChannelId;
                    stored.MsgId = channelPost.MsgId;
                    return stored;
                }

            case TInputMediaAreaChannelPost inputChannelPost:
                {
                    var stored = FromCoordinates(inputChannelPost.Coordinates, StoryMediaAreaType.InputChannelPost);
                    if (inputChannelPost.Channel is TInputChannel inputChannel)
                    {
                        stored.ChannelId = inputChannel.ChannelId;
                    }
                    stored.MsgId = inputChannelPost.MsgId;
                    return stored;
                }

            case TMediaAreaUrl url:
                {
                    var stored = FromCoordinates(url.Coordinates, StoryMediaAreaType.Url);
                    stored.Url = url.Url;
                    return stored;
                }

            case TMediaAreaWeather weather:
                {
                    var stored = FromCoordinates(weather.Coordinates, StoryMediaAreaType.Weather);
                    stored.Emoji = weather.Emoji;
                    stored.Temperature = weather.TemperatureC;
                    stored.Color = weather.Color;
                    return stored;
                }

            case TMediaAreaStarGift starGift:
                {
                    var stored = FromCoordinates(starGift.Coordinates, StoryMediaAreaType.StarGift);
                    stored.Slug = starGift.Slug;
                    return stored;
                }

            default:
                return null;
        }
    }

    public static TVector<IMediaArea>? ToMediaAreas(List<StoryMediaArea>? areas)
    {
        if (areas == null || areas.Count == 0)
        {
            return null;
        }

        var result = new TVector<IMediaArea>();
        foreach (var area in areas)
        {
            var converted = ToMediaArea(area);
            if (converted != null)
            {
                result.Add(converted);
            }
        }

        return result.Count > 0 ? result : null;
    }

    public static IMediaArea? ToMediaArea(StoryMediaArea area)
    {
        var coordinates = ToCoordinates(area);

        switch (area.Type)
        {
            case StoryMediaAreaType.Venue:
                return new TMediaAreaVenue
                {
                    Coordinates = coordinates,
                    Geo = ToGeoPoint(area),
                    Title = area.Title ?? string.Empty,
                    Address = area.Address ?? string.Empty,
                    Provider = area.Provider ?? string.Empty,
                    VenueId = area.VenueId ?? string.Empty,
                    VenueType = area.VenueType ?? string.Empty
                };

            case StoryMediaAreaType.InputVenue:
                return new TInputMediaAreaVenue
                {
                    Coordinates = coordinates,
                    QueryId = area.QueryId ?? 0,
                    ResultId = area.ResultId ?? string.Empty
                };

            case StoryMediaAreaType.GeoPoint:
                return new TMediaAreaGeoPoint
                {
                    Coordinates = coordinates,
                    Geo = ToGeoPoint(area),
                    Address = ToGeoPointAddress(area)
                };

            case StoryMediaAreaType.SuggestedReaction:
                return new TMediaAreaSuggestedReaction
                {
                    Coordinates = coordinates,
                    Dark = area.Dark,
                    Flipped = area.Flipped,
                    Reaction = ToReaction(area)
                };

            case StoryMediaAreaType.ChannelPost:
                return new TMediaAreaChannelPost
                {
                    Coordinates = coordinates,
                    ChannelId = area.ChannelId ?? 0,
                    MsgId = area.MsgId ?? 0
                };

            case StoryMediaAreaType.InputChannelPost:
                return new TMediaAreaChannelPost
                {
                    // Stored from an input constructor, but the client only understands the resolved
                    // form here; channel_id is all inputChannelPost carried anyway.
                    Coordinates = coordinates,
                    ChannelId = area.ChannelId ?? 0,
                    MsgId = area.MsgId ?? 0
                };

            case StoryMediaAreaType.Url:
                return new TMediaAreaUrl
                {
                    Coordinates = coordinates,
                    Url = area.Url ?? string.Empty
                };

            case StoryMediaAreaType.Weather:
                return new TMediaAreaWeather
                {
                    Coordinates = coordinates,
                    Emoji = area.Emoji ?? string.Empty,
                    TemperatureC = area.Temperature ?? 0,
                    Color = area.Color ?? 0
                };

            case StoryMediaAreaType.StarGift:
                return new TMediaAreaStarGift
                {
                    Coordinates = coordinates,
                    Slug = area.Slug ?? string.Empty
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// True when the area points at the given geo position within <paramref name="radiusDegrees"/>.
    /// Used by stories.searchPosts when searching by a location media area.
    /// </summary>
    public static bool MatchesGeo(StoryMediaArea area, double latitude, double longitude, double radiusDegrees)
    {
        if (!area.GeoLat.HasValue || !area.GeoLong.HasValue)
        {
            return false;
        }

        return Math.Abs(area.GeoLat.Value - latitude) <= radiusDegrees &&
               Math.Abs(area.GeoLong.Value - longitude) <= radiusDegrees;
    }

    private static StoryMediaArea FromCoordinates(IMediaAreaCoordinates? coordinates, int type)
    {
        var stored = new StoryMediaArea { Type = type };

        if (coordinates is TMediaAreaCoordinates c)
        {
            stored.X = c.X;
            stored.Y = c.Y;
            stored.W = c.W;
            stored.H = c.H;
            stored.Rotation = c.Rotation;
            stored.Radius = c.Radius;
        }

        return stored;
    }

    private static IMediaAreaCoordinates ToCoordinates(StoryMediaArea area)
    {
        return new TMediaAreaCoordinates
        {
            X = area.X,
            Y = area.Y,
            W = area.W,
            H = area.H,
            Rotation = area.Rotation,
            Radius = area.Radius
        };
    }

    private static void ApplyGeo(StoryMediaArea stored, IGeoPoint? geo)
    {
        if (geo is TGeoPoint point)
        {
            stored.GeoLat = point.Lat;
            stored.GeoLong = point.Long;
            stored.GeoAccessHash = point.AccessHash;
            stored.GeoAccuracyRadius = point.AccuracyRadius;
        }
    }

    private static IGeoPoint ToGeoPoint(StoryMediaArea area)
    {
        if (!area.GeoLat.HasValue || !area.GeoLong.HasValue)
        {
            return new TGeoPointEmpty();
        }

        return new TGeoPoint
        {
            Lat = area.GeoLat.Value,
            Long = area.GeoLong.Value,
            AccessHash = area.GeoAccessHash ?? 0,
            AccuracyRadius = area.GeoAccuracyRadius
        };
    }

    private static IGeoPointAddress? ToGeoPointAddress(StoryMediaArea area)
    {
        if (string.IsNullOrEmpty(area.GeoCountryIso2))
        {
            return null;
        }

        return new TGeoPointAddress
        {
            CountryIso2 = area.GeoCountryIso2,
            State = area.GeoState,
            City = area.GeoCity,
            Street = area.GeoStreet
        };
    }

    private static IReaction ToReaction(StoryMediaArea area)
    {
        if (area.ReactionDocumentId.HasValue)
        {
            return new TReactionCustomEmoji { DocumentId = area.ReactionDocumentId.Value };
        }

        if (!string.IsNullOrEmpty(area.ReactionEmoticon))
        {
            return new TReactionEmoji { Emoticon = area.ReactionEmoticon };
        }

        return new TReactionEmpty();
    }
}
