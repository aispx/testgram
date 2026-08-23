using System.Text;
using System.Text.Json;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Supplies the country code -> form language table served as
/// <c>help.passportConfig.countries_langs</c>, together with the hash clients pass back to get
/// <c>help.passportConfigNotModified</c>.
/// See https://corefork.telegram.org/constructor/help.passportConfig
/// </summary>
public interface IPassportConfigProvider
{
    /// <summary>The JSON object, exactly as it goes into <c>dataJSON.data</c>.</summary>
    string GetCountriesLangs();

    /// <summary>Hash of the current table; never 0, since clients send 0 to mean "I have nothing".</summary>
    int GetHash();
}

public class PassportConfigProvider(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<PassportConfigProvider> logger) : IPassportConfigProvider, ISingletonDependency
{
    private const string EmbeddedResourceName = "MyTelegram.Messenger.Resources.passport-countries-langs.json";

    private readonly Lock _lock = new();
    private string? _cachedPath;
    private string? _json;
    private int _hash;

    public string GetCountriesLangs()
    {
        Load();
        return _json!;
    }

    public int GetHash()
    {
        Load();
        return _hash;
    }

    private void Load()
    {
        var path = options.CurrentValue.Passport.CountriesLangsFile ?? string.Empty;

        lock (_lock)
        {
            if (_json != null && _cachedPath == path)
            {
                return;
            }

            string json;
            try
            {
                json = Compact(ReadTable(path));
            }
            catch (JsonException e)
            {
                logger.LogError(e, "Invalid passport countries_langs table, falling back to the built-in one");
                json = Compact(ReadEmbedded());
            }

            _json = json;
            _hash = ComputeHash(json);
            _cachedPath = path;
        }
    }

    /// <summary>
    /// Re-serialises the table without whitespace. tdlib looks a country up by searching the raw string
    /// for <c>"CC":"</c> (SecureManager.cpp), so a pretty-printed table with a space after the colon
    /// would make every lookup miss. Throws <see cref="JsonException"/> when the table is not an object.
    /// </summary>
    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("countries_langs must be a JSON object");
        }

        return System.Text.Json.JsonSerializer.Serialize(document.RootElement,
            new JsonSerializerOptions { WriteIndented = false });
    }

    private string ReadTable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ReadEmbedded();
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Cannot read passport countries_langs from {Path}, using the built-in table", path);
            return ReadEmbedded();
        }
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(PassportConfigProvider).Assembly.GetManifestResourceStream(EmbeddedResourceName)
                           ?? throw new InvalidOperationException($"Missing embedded resource {EmbeddedResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// The hash is an opaque change token; clients only ever compare it for equality. 0 is reserved for
    /// "client has no cached config", so it is never produced.
    /// </summary>
    private static int ComputeHash(string json)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var hash = BitConverter.ToInt32(digest, 0);

        return hash == 0 ? 1 : hash;
    }
}
