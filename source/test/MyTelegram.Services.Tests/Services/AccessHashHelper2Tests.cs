using Microsoft.Extensions.Configuration;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Services;

/// <summary>
/// The access hash format is fixed by the deployed closed-source images, not by this repository.
///
/// <para><c>session-server</c> and <c>file-server</c> validate the hash before a request reaches the
/// messenger — for 123 request types, including <c>upload.getFile</c> and <c>messages.sendMedia</c> —
/// and both are NativeAOT builds of an older revision of
/// <see cref="AccessHashHelper2.GenerateAccessHash"/>. So these tests pin the wire format rather than
/// the properties one would want from it: a "better" hash is one the validator rejects, which shows up
/// as a media download that never starts.</para>
/// </summary>
public class AccessHashHelper2Tests
{
    private const long CurrentUserId = 2012001;
    private const long AccessHashKeyId = 6792106890083689059;

    /// <summary>
    /// Byte-for-byte agreement with the deployed validator, expressed as the values it accepts for a
    /// known secret. Changing the layout changes these numbers, and a build that changes them cannot
    /// download a single file in this deployment.
    /// </summary>
    [Theory]
    [InlineData(1001, AccessHashType.User, -167721578767644113)]
    [InlineData(1001, AccessHashType.Document, -167721578767644113)]
    [InlineData(1002, AccessHashType.User, -5316184929004023002)]
    public void AccessHash_MatchesTheFormatTheDeployedValidatorEnforces(long targetId,
        AccessHashType accessHashType, long expected)
    {
        CreateSut().GenerateAccessHash(CurrentUserId, AccessHashKeyId, targetId, accessHashType)
            .ShouldBe(expected);
    }

    [Fact]
    public async Task AccessHash_ShouldDependOnTargetId()
    {
        // The one property the deployed format does carry: the hash is the capability token proving
        // the client legitimately learned of a specific object, so a token for one object must not
        // open another.
        var sut = CreateSut();

        var first = sut.GenerateAccessHash(CurrentUserId, AccessHashKeyId, 1001, AccessHashType.User);
        var second = sut.GenerateAccessHash(CurrentUserId, AccessHashKeyId, 1002, AccessHashType.User);

        first.ShouldNotBe(second);

        (await sut.IsAccessHashValidAsync(CurrentUserId, AccessHashKeyId, 1002, first, AccessHashType.User))
            .ShouldBeFalse();
    }

    /// <summary>
    /// The session key is the only other thing that binds the hash, which is what keeps one user's
    /// tokens from being usable by another.
    /// </summary>
    [Fact]
    public void AccessHash_ShouldDependOnTheSessionAccessHashKey()
    {
        var sut = CreateSut();

        sut.GenerateAccessHash(CurrentUserId, AccessHashKeyId, 1001, AccessHashType.User)
            .ShouldNotBe(sut.GenerateAccessHash(CurrentUserId, AccessHashKeyId + 1, 1001, AccessHashType.User));
    }

    /// <summary>
    /// Documented weakness of the deployed format, asserted so that a future "fix" is a deliberate
    /// decision rather than a surprise: writing <c>accessHashKeyId</c> over the buffer clobbers the
    /// type byte and the low seven bytes of <c>currentUserId</c>, so a hash minted for one type or one
    /// user id validates for the other. It cannot be tightened here — the enforcing validator is a
    /// stripped native binary — and commit 3d390cd31, which did tighten it, broke every media
    /// download in this deployment until it was reverted.
    /// </summary>
    [Fact]
    public async Task AccessHash_DoesNotBindTheTypeOrTheUserId_AsTheDeployedFormatDoesNot()
    {
        var sut = CreateSut();

        var userHash = sut.GenerateAccessHash(CurrentUserId, AccessHashKeyId, 1001, AccessHashType.User);

        sut.GenerateAccessHash(CurrentUserId, AccessHashKeyId, 1001, AccessHashType.Document)
            .ShouldBe(userHash);
        sut.GenerateAccessHash(CurrentUserId + 1, AccessHashKeyId, 1001, AccessHashType.User)
            .ShouldBe(userHash);

        // Which is also why phone calls validate in the group-call lane the deployed images use for
        // inputPhoneCall, with no special case needed for it.
        (await sut.IsAccessHashValidAsync(CurrentUserId, AccessHashKeyId, 1001, userHash, AccessHashType.Call))
            .ShouldBeTrue();
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
