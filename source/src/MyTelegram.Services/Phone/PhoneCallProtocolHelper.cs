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

    /// <summary>
    /// Returns <c>true</c> when the peer's <c>[min_layer, max_layer]</c> window is well-formed and overlaps
    /// the window this server supports (<see cref="LegacyMinLayer"/>..<see cref="LegacyMaxLayer"/>).
    /// </summary>
    /// <remarks>
    /// This deliberately checks for an overlap rather than exact equality. The official apps send 65/92
    /// (Android <c>VoIPService.CALL_MIN_LAYER</c> + <c>Instance.getConnectionMaxLayer()</c>; tdesktop
    /// <c>kMinLayer</c> + <c>tgcalls::Meta::MaxLayer()</c>), but TDLib's <c>CallProtocol</c> defaults to
    /// <c>min_layer = max_layer = 65</c>, and an equality check would reject every stock TDLib client with
    /// <c>CALL_PROTOCOL_LAYER_INVALID</c>.
    /// </remarks>
    public static bool HasValidLegacyLayers(IPhoneCallProtocol? protocol)
    {
        return protocol != null
               && protocol.MinLayer <= protocol.MaxLayer
               && protocol.MinLayer <= LegacyMaxLayer
               && protocol.MaxLayer >= LegacyMinLayer;
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
    /// Attempts to select the negotiated tgcalls version supported by both peers: the greatest version in
    /// the intersection under <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    /// <remarks>
    /// Both official clients treat <c>library_versions[0]</c> of the server's reply as <em>the</em> version
    /// to instantiate — tdesktop <c>Call::createAndStartController</c> reads
    /// <c>vlibrary_versions().value(0, "2.4.4")</c> and fails the call outright when
    /// <c>tgcalls::Meta::Create</c> does not know it; Android passes
    /// <c>privateCall.protocol.library_versions.get(0)</c> straight to <c>Instance.makeInstance</c>.
    /// <para>
    /// Ordinal ordering is not arbitrary: it is the same scale the clients themselves rank versions on.
    /// Android gates video availability on
    /// <c>"2.7.7".compareTo(library_versions.get(0)) &lt;= 0</c> (a lexicographic <c>String.compareTo</c>),
    /// destroying the video capturer and reporting <c>isVideoAvailable = false</c> when it fails. Since
    /// <c>tgcalls::Meta::Versions()</c> enumerates a <c>std::map</c>, Android advertises
    /// <c>["10.0.0", "11.0.0", "12.0.0", "13.0.0", "2.4.4", "2.7.7", "5.0.0", ...]</c>; picking the callee's
    /// first entry would select <c>"10.0.0"</c> and silently downgrade every video call to audio. Ordinal
    /// max selects <c>"9.0.0"</c>, which clears Android's gate and is also tdesktop's own top preference
    /// (it advertises <c>Meta::Versions()</c> reversed).
    /// </para>
    /// </remarks>
    public static bool TryGetCommonLibraryVersion(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions,
        out string? commonVersion)
    {
        var callerVersions = new HashSet<string>(
            NormalizeVersions(callerLibraryVersions),
            StringComparer.Ordinal);
        commonVersion = NormalizeVersions(calleeLibraryVersions)
            .Where(callerVersions.Contains)
            .OrderByDescending(version => version, StringComparer.Ordinal)
            .FirstOrDefault();
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
