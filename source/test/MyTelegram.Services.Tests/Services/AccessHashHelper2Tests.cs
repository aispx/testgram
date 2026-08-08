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

    [Fact]
    public void AccessHash_ShouldDependOnTargetId()
    {
        // The access hash is the capability token proving the client legitimately learned of a
        // specific peer. If targetId does not affect it, one hash validates for every object.
        var sut = CreateSut();
        const long currentUserId = 2012001;
        const long accessHashKeyId = 6792106890083689059;

        var first = sut.GenerateAccessHash(currentUserId, accessHashKeyId, 1001, AccessHashType.User);
        var second = sut.GenerateAccessHash(currentUserId, accessHashKeyId, 1002, AccessHashType.User);

        first.ShouldNotBe(second);
    }

    [Fact]
    public void AccessHash_ShouldDependOnCurrentUserId()
    {
        var sut = CreateSut();
        const long accessHashKeyId = 6792106890083689059;
        const long targetId = 1001;

        var first = sut.GenerateAccessHash(2012001, accessHashKeyId, targetId, AccessHashType.User);
        var second = sut.GenerateAccessHash(2012002, accessHashKeyId, targetId, AccessHashType.User);

        first.ShouldNotBe(second);
    }

    [Fact]
    public void AccessHash_ShouldDependOnAccessHashType()
    {
        var sut = CreateSut();
        const long currentUserId = 2012001;
        const long accessHashKeyId = 6792106890083689059;
        const long targetId = 1001;

        var userHash = sut.GenerateAccessHash(currentUserId, accessHashKeyId, targetId, AccessHashType.User);
        var wallPaperHash = sut.GenerateAccessHash(currentUserId, accessHashKeyId, targetId, AccessHashType.WallPaper);

        userHash.ShouldNotBe(wallPaperHash);
    }

    [Fact]
    public async Task AccessHash_ForOneTarget_ShouldNotValidateForAnother()
    {
        var sut = CreateSut();
        const long currentUserId = 2012001;
        const long accessHashKeyId = 6792106890083689059;

        var hashForOwnTarget = sut.GenerateAccessHash(currentUserId, accessHashKeyId, 1001, AccessHashType.User);

        var isValidForOtherTarget = await sut.IsAccessHashValidAsync(
            currentUserId,
            accessHashKeyId,
            1002,
            hashForOwnTarget,
            AccessHashType.User);

        isValidForOtherTarget.ShouldBeFalse();
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
