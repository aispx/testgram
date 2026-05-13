using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using MyTelegram.Converters.Services.Interfaces;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Converters.Services.Impl;

internal sealed class SessionLocationResolver : ISessionLocationResolver, ITransientDependency, IDisposable
{
    private static readonly string[] CountryCodeKeys =
    [
        "country_code", "countrycode", "countryCode", "cf-ipcountry", "cloudfront-viewer-country", "x-country-code", "x-country"
    ];

    private static readonly string[] CountryNameKeys =
    [
        "country_name", "countryname", "countryName", "country"
    ];

    private static readonly string[] RegionKeys =
    [
        "region", "region_name", "regionname", "regionName", "state", "province"
    ];

    private static readonly string[] DatabasePathEnvKeys =
    [
        "MYTELEGRAM_GEOIP_COUNTRY_DB",
        "GEOLITE2_COUNTRY_DB",
        "MAXMIND_COUNTRY_DB"
    ];

    private static readonly string[] DefaultDatabasePaths =
    [
        "/usr/share/GeoIP/GeoLite2-Country.mmdb",
        "/usr/share/GeoIP/GeoLite2-City.mmdb",
        "/usr/local/share/GeoIP/GeoLite2-Country.mmdb",
        "/usr/local/share/GeoIP/GeoLite2-City.mmdb",
        "/app/GeoLite2-Country.mmdb",
        "/app/GeoLite2-City.mmdb"
    ];

    private readonly ConcurrentDictionary<string, SessionLocation> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<DatabaseReader?> _databaseReader;
    private readonly Lazy<bool> _useCityLookup;

    public SessionLocationResolver()
    {
        var dbPath = ResolveDatabasePath();
        _databaseReader = new Lazy<DatabaseReader?>(() =>
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return null;
            }

            return new DatabaseReader(dbPath);
        });
        _useCityLookup = new Lazy<bool>(() => !string.IsNullOrWhiteSpace(dbPath) && dbPath.Contains("city", StringComparison.OrdinalIgnoreCase));
    }

    public SessionLocation Resolve(IDeviceReadModel deviceReadModel)
    {
        if (TryResolveFromParameters(deviceReadModel.Parameters, out var location))
        {
            return location;
        }

        var ip = deviceReadModel.Ip?.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            return SessionLocationEmpty;
        }

        return _cache.GetOrAdd(ip, ResolveByIpCore);
    }

    public void Dispose()
    {
        if (_databaseReader.IsValueCreated)
        {
            _databaseReader.Value?.Dispose();
        }
    }

    private SessionLocation ResolveByIpCore(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return SessionLocationEmpty;
        }

        if (IsPrivateOrLocal(address))
        {
            return new SessionLocation("Local Network", string.Empty);
        }

        var databaseReader = _databaseReader.Value;
        if (databaseReader == null)
        {
            return SessionLocationEmpty;
        }

        try
        {
            if (_useCityLookup.Value)
            {
                var response = databaseReader.City(address);
                return new SessionLocation(
                    NormalizeCountry(response.Country?.IsoCode, response.Country?.Name),
                    NormalizeRegion(response.MostSpecificSubdivision?.Name));
            }

            var country = databaseReader.Country(address);
            return new SessionLocation(
                NormalizeCountry(country.Country?.IsoCode, country.Country?.Name),
                string.Empty);
        }
        catch (AddressNotFoundException)
        {
            return SessionLocationEmpty;
        }
        catch
        {
            return SessionLocationEmpty;
        }
    }

    private static bool TryResolveFromParameters(Dictionary<string, string>? parameters, out SessionLocation location)
    {
        location = SessionLocationEmpty;
        if (parameters == null || parameters.Count == 0)
        {
            return false;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                normalized[key.Trim()] = value.Trim();
            }
        }

        if (normalized.Count == 0)
        {
            return false;
        }

        string? countryCode = FindFirst(normalized, CountryCodeKeys);
        string? countryName = FindFirst(normalized, CountryNameKeys);
        string region = NormalizeRegion(FindFirst(normalized, RegionKeys));

        var country = NormalizeCountry(countryCode, countryName);
        if (string.IsNullOrEmpty(country) && string.IsNullOrEmpty(region))
        {
            return false;
        }

        location = new SessionLocation(country, region);
        return true;
    }

    private static string? FindFirst(IReadOnlyDictionary<string, string> values, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeCountry(string? countryCode, string? countryName)
    {
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            var normalizedCode = countryCode.Trim().ToUpperInvariant();
            if (normalizedCode is not ("XX" or "T1" or "A1" or "A2" or "O1" or "AP"))
            {
                try
                {
                    if (normalizedCode.Length == 2)
                    {
                        return new RegionInfo(normalizedCode).EnglishName;
                    }
                }
                catch
                {
                    // ignore and fall back to country name
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(countryName))
        {
            var normalizedName = countryName.Trim();
            if (!normalizedName.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedName;
            }
        }

        return string.Empty;
    }

    private static string NormalizeRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return string.Empty;
        }

        var normalized = region.Trim();
        return normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized;
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false
            };
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || IsUniqueLocal(address);
        }

        return false;
    }

    private static bool IsUniqueLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length > 0 && (bytes[0] & 0xfe) == 0xfc;
    }

    private static string? ResolveDatabasePath()
    {
        foreach (var envKey in DatabasePathEnvKeys)
        {
            var envValue = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(envValue) && File.Exists(envValue))
            {
                return envValue;
            }
        }

        return DefaultDatabasePaths.FirstOrDefault(File.Exists);
    }

    private static readonly SessionLocation SessionLocationEmpty = new(string.Empty, string.Empty);
}
