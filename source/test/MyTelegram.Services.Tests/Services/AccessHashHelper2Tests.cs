using Microsoft.Extensions.Configuration;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Services;

public class AccessHashHelper2Tests
{
    [Fact]
    public async Task CallAccessHash_ShouldUseLegacyGroupCallLane()
    {
        var sut = CreateSut();
        const long currentUserId = 2012001;
        const long accessHashKeyId = 6792106890083689059;
        const long callId = 4332412614108320829;

        var callHash = sut.GenerateAccessHash(currentUserId, accessHashKeyId, callId, AccessHashType.Call);
        var groupCallHash = sut.GenerateAccessHash(currentUserId, accessHashKeyId, callId, AccessHashType.GroupCall);

        callHash.ShouldBe(groupCallHash);
        var isValidAsCall = await sut.IsAccessHashValidAsync(
            currentUserId,
            accessHashKeyId,
            callId,
            groupCallHash,
            AccessHashType.Call);
        isValidAsCall.ShouldBeTrue();
    }

    private static AccessHashHelper2 CreateSut()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:AccessHashSecretKey"] = "test-secret-key"
            })
            .Build();

        return new AccessHashHelper2(configuration, new PeerHelper());
    }
}
