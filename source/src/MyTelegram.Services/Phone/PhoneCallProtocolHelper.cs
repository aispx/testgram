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

    /// <summary>
    /// Returns <c>true</c> when the caller and callee <c>library_versions</c> lists share at least one
    /// common tgcalls version. An empty intersection means the two peers cannot agree on a protocol
    /// version and the call must be rejected with <c>CALL_PROTOCOL_COMPAT_LAYER_INVALID</c>.
    /// </summary>
    public static bool HasCommonLibraryVersion(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions)
    {
        return TryGetCommonLibraryVersion(callerLibraryVersions, calleeLibraryVersions, out _);
    }

    /// <summary>
    /// Attempts to select the negotiated tgcalls version supported by both peers, following the callee's
    /// preference order (the callee is the side that finalises the negotiation in <c>phone.acceptCall</c>).
    /// </summary>
    public static bool TryGetCommonLibraryVersion(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions,
        out string? commonVersion)
    {
        var callerVersions = new HashSet<string>(
            NormalizeVersions(callerLibraryVersions),
            StringComparer.Ordinal);
        commonVersion = NormalizeVersions(calleeLibraryVersions)
            .FirstOrDefault(callerVersions.Contains);
        return commonVersion != null;
    }

    /// <summary>
    /// Returns <c>true</c> when the peer explicitly advertised at least one tgcalls <c>library_versions</c>
    /// entry. Only clients that negotiate via <c>library_versions</c> (as opposed to legacy
    /// <c>min_layer</c>/<c>max_layer</c> only) can be upgraded to a conference call, so this signals
    /// whether the peer supports the <c>conference_supported</c> upgrade path.
    /// </summary>
    public static bool AdvertisesConferenceSupport(IPhoneCallProtocol? protocol)
    {
        var versions = protocol?.LibraryVersions;
        return versions != null && versions.Any(version => !string.IsNullOrWhiteSpace(version));
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
        return Negotiate(callerLibraryVersions, GetLibraryVersions(calleeProtocol));
    }

    public static TPhoneCallProtocol Negotiate(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions)
    {
        return TryGetCommonLibraryVersion(callerLibraryVersions, calleeLibraryVersions, out var selectedVersion)
            ? FromLibraryVersions([selectedVersion!])
            : FromLibraryVersions(NormalizeVersions(calleeLibraryVersions));
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
