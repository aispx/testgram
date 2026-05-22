using MyTelegram.Schema;

namespace MyTelegram.Services.Phone;

public static class PhoneCallProtocolHelper
{
    public const int LegacyMinLayer = 65;
    public const int LegacyMaxLayer = 92;

    private static readonly string[] DefaultLibraryVersions = ["2.7.7"];

    public static bool HasValidLegacyFlags(IPhoneCallProtocol? protocol)
    {
        return protocol is { UdpP2p: true, UdpReflector: true };
    }

    public static bool HasValidLegacyLayers(IPhoneCallProtocol? protocol)
    {
        return protocol is { MinLayer: LegacyMinLayer, MaxLayer: LegacyMaxLayer };
    }

    public static IReadOnlyList<string> GetLibraryVersions(IPhoneCallProtocol? protocol)
    {
        return NormalizeVersions(protocol?.LibraryVersions);
    }

    public static TPhoneCallProtocol Normalize(IPhoneCallProtocol? protocol)
    {
        return FromLibraryVersions(GetLibraryVersions(protocol));
    }

    public static TPhoneCallProtocol FromLibraryVersions(IEnumerable<string>? libraryVersions)
    {
        return new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = LegacyMinLayer,
            MaxLayer = LegacyMaxLayer,
            LibraryVersions = new TVector<string>(NormalizeVersions(libraryVersions))
        };
    }

    public static TPhoneCallProtocol Negotiate(
        IEnumerable<string>? callerLibraryVersions,
        IPhoneCallProtocol? calleeProtocol)
    {
        var callerVersions = new HashSet<string>(
            NormalizeVersions(callerLibraryVersions),
            StringComparer.Ordinal);
        var calleeVersions = GetLibraryVersions(calleeProtocol);
        var selectedVersion = calleeVersions.FirstOrDefault(callerVersions.Contains);

        return selectedVersion == null
            ? FromLibraryVersions(calleeVersions)
            : FromLibraryVersions([selectedVersion]);
    }

    private static IReadOnlyList<string> NormalizeVersions(IEnumerable<string>? libraryVersions)
    {
        if (libraryVersions == null)
        {
            return DefaultLibraryVersions;
        }

        var versions = libraryVersions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return versions.Length == 0 ? DefaultLibraryVersions : versions;
    }
}
