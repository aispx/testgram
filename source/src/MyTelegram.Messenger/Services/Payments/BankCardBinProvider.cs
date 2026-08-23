using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyTelegram.Messenger.Services.Payments;

public sealed record BankCardBinEntry(string Title, string? Url);

/// <summary>
/// Resolves a card number to its issuer for <c>payments.getBankCardData</c>.
/// </summary>
/// <remarks>
/// Backed by a local BIN table rather than an external lookup service, so no card number ever leaves
/// this server. The built-in table ships as an embedded resource and can be replaced wholesale
/// through <c>App__Payments__BankBinsFile</c> — the same arrangement Passport's countries_langs uses.
/// See https://corefork.telegram.org/method/payments.getBankCardData
/// </remarks>
public interface IBankCardBinProvider
{
    /// <summary>The issuer behind <paramref name="cardNumber"/>, or null when the BIN is unknown.</summary>
    BankCardBinEntry? Resolve(string? cardNumber);
}

public class BankCardBinProvider(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<BankCardBinProvider> logger) : IBankCardBinProvider, ISingletonDependency
{
    private const string EmbeddedResourceName = "MyTelegram.Messenger.Resources.bank-bins.json";

    /// <summary>Shortest and longest real BIN prefixes; anything outside cannot be a card number.</summary>
    private const int MinCardDigits = 12;
    private const int MaxCardDigits = 19;

    /// <summary>Longest BIN prefix the table stores.</summary>
    private const int MaxPrefixLength = 8;

    /// <summary>Longest payment network prefix (Elo/Verve ranges are 6 digits wide).</summary>
    private const int MaxNetworkPrefixLength = 6;

    private readonly Lock _lock = new();
    private string? _cachedPath;
    private BinTable? _table;

    public BankCardBinEntry? Resolve(string? cardNumber)
    {
        var digits = OnlyDigits(cardNumber);
        if (digits.Length is < MinCardDigits or > MaxCardDigits || !PassesLuhn(digits))
        {
            return null;
        }

        var table = Load();

        // Longest prefix wins, so an issuer specific BIN beats the network wide one it sits under.
        for (var length = Math.Min(digits.Length, MaxPrefixLength); length > 0; length--)
        {
            if (table.Bins.TryGetValue(digits[..length], out var index))
            {
                return table.Issuers[index];
            }
        }

        // No issuer on file. The card is still a valid card, so it is named by its payment network
        // (ISO/IEC 7812 IIN ranges) rather than reported as invalid.
        for (var length = Math.Min(digits.Length, MaxNetworkPrefixLength); length > 0; length--)
        {
            if (table.Networks.TryGetValue(digits[..length], out var network))
            {
                return new BankCardBinEntry(network, null);
            }
        }

        return null;
    }

    private sealed record BinTable(
        IReadOnlyDictionary<string, string> Networks,
        IReadOnlyList<BankCardBinEntry> Issuers,
        IReadOnlyDictionary<string, int> Bins);

    private static string OnlyDigits(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (c is not (' ' or '-'))
            {
                // Anything other than a separator means this is not a card number at all.
                return string.Empty;
            }
        }

        return builder.ToString();
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubled = false;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubled)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }

    private BinTable Load()
    {
        var path = options.CurrentValue.Payments.BankBinsFile ?? string.Empty;

        lock (_lock)
        {
            if (_table != null && _cachedPath == path)
            {
                return _table;
            }

            BinTable table;
            try
            {
                table = Parse(ReadTable(path));
            }
            catch (JsonException e)
            {
                logger.LogError(e, "Invalid bank BIN table, falling back to the built-in one");
                table = Parse(ReadEmbedded());
            }

            _table = table;
            _cachedPath = path;

            return table;
        }
    }

    /// <summary>
    /// Reads the table. Issuers are interned and referenced by index from <c>bins</c>: the same few
    /// thousand banks back a hundred thousand prefixes, so storing them once keeps the file at a
    /// couple of megabytes instead of tens.
    /// </summary>
    private static BinTable Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("issuers", out var issuersElement) ||
            issuersElement.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("bins", out var binsElement) ||
            binsElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("bank BIN table must be an object with \"issuers\" and \"bins\"");
        }

        var issuers = new List<BankCardBinEntry>(issuersElement.GetArrayLength());
        foreach (var issuer in issuersElement.EnumerateArray())
        {
            if (issuer.ValueKind != JsonValueKind.Array || issuer.GetArrayLength() == 0)
            {
                throw new JsonException("every issuer must be a [title, url] pair");
            }

            var title = issuer[0].GetString() ?? string.Empty;
            var url = issuer.GetArrayLength() > 1 ? issuer[1].GetString() : null;
            issuers.Add(new BankCardBinEntry(title, string.IsNullOrEmpty(url) ? null : url));
        }

        var bins = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in binsElement.EnumerateObject())
        {
            if (!property.Name.All(char.IsAsciiDigit) || !property.Value.TryGetInt32(out var index))
            {
                continue;
            }

            if ((uint)index < (uint)issuers.Count && issuers[index].Title.Length > 0)
            {
                bins[property.Name] = index;
            }
        }

        var networks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("networks", out var networksElement) &&
            networksElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in networksElement.EnumerateObject())
            {
                var name = property.Value.GetString();
                if (property.Name.All(char.IsAsciiDigit) && !string.IsNullOrEmpty(name))
                {
                    networks[property.Name] = name;
                }
            }
        }

        return new BinTable(networks, issuers, bins);
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
            logger.LogError(e, "Cannot read bank BIN table from {Path}, using the built-in one", path);
            return ReadEmbedded();
        }
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(BankCardBinProvider).Assembly.GetManifestResourceStream(EmbeddedResourceName)
                           ?? throw new InvalidOperationException($"Missing embedded resource {EmbeddedResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
