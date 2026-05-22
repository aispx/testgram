using MyTelegram.Schema;
using MyTelegram.Services.Phone;

namespace MyTelegram.Services.Tests.Phone;

public class PhoneCallProtocolHelperTests
{
    [Fact]
    public void Normalize_ShouldKeepLibraryOrderAndUseLegacyProtocolValues()
    {
        var protocol = new TPhoneCallProtocol
        {
            MinLayer = 1,
            MaxLayer = 2,
            LibraryVersions = new TVector<string> { "v3", "v2", "v3" }
        };

        var result = PhoneCallProtocolHelper.Normalize(protocol);

        result.UdpP2p.ShouldBeTrue();
        result.UdpReflector.ShouldBeTrue();
        result.MinLayer.ShouldBe(PhoneCallProtocolHelper.LegacyMinLayer);
        result.MaxLayer.ShouldBe(PhoneCallProtocolHelper.LegacyMaxLayer);
        result.LibraryVersions.ShouldBe(new[] { "v3", "v2" });
    }

    [Fact]
    public void Negotiate_ShouldReturnSingleSharedVersionInCalleePreferenceOrder()
    {
        var calleeProtocol = new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = PhoneCallProtocolHelper.LegacyMinLayer,
            MaxLayer = PhoneCallProtocolHelper.LegacyMaxLayer,
            LibraryVersions = new TVector<string> { "v3", "v2", "v1" }
        };

        var result = PhoneCallProtocolHelper.Negotiate(["v1", "v2"], calleeProtocol);

        result.LibraryVersions.ShouldBe(new[] { "v2" });
    }
}
