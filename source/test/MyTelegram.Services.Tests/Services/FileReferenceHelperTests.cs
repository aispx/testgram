using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Services;

/// <summary>
/// The <c>file_reference</c> format, see https://corefork.telegram.org/api/file-references .
///
/// <para>Unlike the access hash, this layout is <b>ours</b> — no deployed binary validates it — so these
/// tests pin the properties the API depends on rather than a byte pattern: a reference only opens the
/// media it was minted for, it expires, it is stable for long enough that response hashes do not churn,
/// and the error strings are the ones clients parse.</para>
/// </summary>
public class FileReferenceHelperTests
{
    private const long DocumentId = 5350513349223189212;
    private const long Now = 1_800_000_000;
    private const int TtlHours = 48;
    private const int TtlSeconds = TtlHours * 3600;

    [Fact]
    public void Reference_RoundTrips()
    {
        var sut = CreateSut();

        var reference = sut.CreateAt(AccessHashType.Document, DocumentId, Now);

        reference.Length.ShouldBe(FileReferenceHelper.ReferenceLength);
        sut.ValidateAt(reference, AccessHashType.Document, DocumentId, Now)
            .ShouldBe(FileReferenceState.Valid);
    }

    /// <summary>
    /// A reference for one media file must not open another, which is the whole point of signing it.
    /// The type is in the signature too, so a photo reference cannot be replayed as a document one of
    /// the same id — ids of the two are allocated from different counters and do collide.
    /// </summary>
    [Fact]
    public void Reference_BindsTheIdAndTheType()
    {
        var sut = CreateSut();

        var reference = sut.CreateAt(AccessHashType.Document, DocumentId, Now);

        sut.ValidateAt(reference, AccessHashType.Document, DocumentId + 1, Now)
            .ShouldBe(FileReferenceState.Invalid);
        sut.ValidateAt(reference, AccessHashType.Photo, DocumentId, Now)
            .ShouldBe(FileReferenceState.Invalid);
    }

    [Fact]
    public void Reference_BindsTheSecret()
    {
        var reference = CreateSut().CreateAt(AccessHashType.Document, DocumentId, Now);

        CreateSut(secret: "another-secret")
            .ValidateAt(reference, AccessHashType.Document, DocumentId, Now)
            .ShouldBe(FileReferenceState.Invalid);
    }

    [Fact]
    public void TamperedReference_IsInvalid()
    {
        var sut = CreateSut();

        var reference = sut.CreateAt(AccessHashType.Document, DocumentId, Now);
        reference[^1] ^= 0x01;

        sut.ValidateAt(reference, AccessHashType.Document, DocumentId, Now)
            .ShouldBe(FileReferenceState.Invalid);
    }

    /// <summary>
    /// Clients normalise a missing reference to an empty byte array and send it as is — Android's
    /// <c>FileLoadOperation</c> does exactly that — so empty is a state of its own with its own error,
    /// not a malformed value.
    /// </summary>
    [Fact]
    public void EmptyReference_IsEmptyRatherThanInvalid()
    {
        CreateSut().ValidateAt([], AccessHashType.Document, DocumentId, Now)
            .ShouldBe(FileReferenceState.Empty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(21)]
    public void ReferenceOfTheWrongLength_IsInvalid(int length)
    {
        CreateSut().ValidateAt(new byte[length], AccessHashType.Document, DocumentId, Now)
            .ShouldBe(FileReferenceState.Invalid);
    }

    /// <summary>
    /// The lifetime is measured from the issue timestamp carried in the clear, so the boundary is exact.
    /// </summary>
    [Fact]
    public void Reference_ExpiresAfterTheConfiguredLifetime()
    {
        var sut = CreateSut();

        var reference = sut.CreateAt(AccessHashType.Document, DocumentId, Now);
        var issuedAt = ReadIssuedAt(reference);

        sut.ValidateAt(reference, AccessHashType.Document, DocumentId, issuedAt + TtlSeconds)
            .ShouldBe(FileReferenceState.Valid);
        sut.ValidateAt(reference, AccessHashType.Document, DocumentId, issuedAt + TtlSeconds + 1)
            .ShouldBe(FileReferenceState.Expired);
    }

    /// <summary>
    /// The issue timestamp is quantised to half the lifetime, so a reference is byte-identical for
    /// everyone who asks within the same window. <c>help.getAppConfig</c> depends on it:
    /// <c>emojies_sounds</c> carries a reference per document and its bytes are folded into the config
    /// hash, so a reference that changed per call would mean <c>appConfigNotModified</c> never fires.
    /// </summary>
    [Fact]
    public void Reference_IsStableWithinItsWindow()
    {
        var sut = CreateSut();

        var first = sut.CreateAt(AccessHashType.Document, DocumentId, Now);
        var later = sut.CreateAt(AccessHashType.Document, DocumentId, Now + 3600);

        later.ShouldBe(first);
    }

    /// <summary>
    /// The other half of quantising: whatever moment a client fetches in, it receives a reference with at
    /// least half the lifetime still on it, so an ordinary session never meets an expiry mid-download.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    [InlineData(TtlSeconds / 2 - 1)]
    public void Reference_IsAlwaysGoodForAtLeastHalfTheLifetime(int secondsIntoTheWindow)
    {
        var sut = CreateSut();
        var mintedAt = Now - Now % (TtlSeconds / 2) + secondsIntoTheWindow;

        var reference = sut.CreateAt(AccessHashType.Document, DocumentId, mintedAt);

        sut.ValidateAt(reference, AccessHashType.Document, DocumentId, mintedAt + TtlSeconds / 2)
            .ShouldBe(FileReferenceState.Valid);
    }

    /// <summary>
    /// A signature that validates for a timestamp well in the future can only come from a forged value,
    /// and treating it as valid would hand out an unexpiring reference. The tolerance covers clock drift
    /// between the host that mints and the host that checks — they are different containers.
    /// </summary>
    [Fact]
    public void ReferenceFromTheFuture_IsInvalid()
    {
        var sut = CreateSut();

        var reference = sut.CreateAt(AccessHashType.Document, DocumentId, Now + TtlSeconds);

        sut.ValidateAt(reference, AccessHashType.Document, DocumentId, Now)
            .ShouldBe(FileReferenceState.Invalid);
    }

    /// <summary>
    /// The plain error names, as listed by every method that can refuse a reference — for example
    /// https://corefork.telegram.org/method/upload.getFile .
    /// </summary>
    [Fact]
    public void Check_AnswersTheDocumentedErrors()
    {
        var sut = CreateSut();

        ThrownBy(() => sut.Check([], AccessHashType.Document, DocumentId))
            .ShouldBe("FILE_REFERENCE_EMPTY");
        ThrownBy(() => sut.Check(Tampered(sut), AccessHashType.Document, DocumentId))
            .ShouldBe("FILE_REFERENCE_INVALID");
        ThrownBy(() => sut.Check(LongExpired(sut), AccessHashType.Document, DocumentId))
            .ShouldBe("FILE_REFERENCE_EXPIRED");
    }

    /// <summary>
    /// The indexed forms name the offending entry of a <c>multi_media</c> or <c>extended_media</c> vector.
    /// The spelling matters: tdlib's <c>get_file_reference_error_source</c> takes the digits straight
    /// after the <c>FILE_REFERENCE_</c> prefix, and Android's <c>getFileRefErrorIndex</c> only parses an
    /// index out of a string that also ends in <c>_EXPIRED</c>.
    /// </summary>
    [Fact]
    public void Check_AnswersTheIndexedErrorsClientsParseAnIndexOutOf()
    {
        var sut = CreateSut();

        ThrownBy(() => sut.Check(LongExpired(sut), AccessHashType.Document, DocumentId, index: 0))
            .ShouldBe("FILE_REFERENCE_0_EXPIRED");
        ThrownBy(() => sut.Check(LongExpired(sut), AccessHashType.Document, DocumentId, index: 3))
            .ShouldBe("FILE_REFERENCE_3_EXPIRED");
        ThrownBy(() => sut.Check(Tampered(sut), AccessHashType.Document, DocumentId, index: 2))
            .ShouldBe("FILE_REFERENCE_2_INVALID");

        // messages.sendMultiMedia documents an indexed empty form too, even though the generated
        // RpcErrors list carries only the expired and invalid ones.
        ThrownBy(() => sut.Check([], AccessHashType.Document, DocumentId, index: 1))
            .ShouldBe("FILE_REFERENCE_1_EMPTY");
    }

    /// <summary>
    /// The mode that makes the first deployment survivable: an emit path still handing out a stale or
    /// empty reference shows up as a log line instead of as media no client can load.
    /// </summary>
    [Theory]
    [InlineData(FileReferenceMode.Off)]
    [InlineData(FileReferenceMode.LogOnly)]
    public void Check_LetsABadReferenceThroughUnlessEnforcing(FileReferenceMode mode)
    {
        var sut = CreateSut(mode: mode);

        sut.Check([], AccessHashType.Document, DocumentId);
        sut.Check(Tampered(sut), AccessHashType.Document, DocumentId);
        sut.Check(LongExpired(sut), AccessHashType.Document, DocumentId, index: 1);
    }

    [Fact]
    public void Check_AcceptsAFreshReference()
    {
        var sut = CreateSut();

        sut.Check(sut.Create(AccessHashType.Document, DocumentId), AccessHashType.Document, DocumentId);
    }

    /// <summary>
    /// So an existing deployment starts issuing real references without a new environment variable.
    /// </summary>
    [Fact]
    public void Secret_FallsBackToTheAccessHashSecret()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:AccessHashSecretKey"] = "test-secret-key"
            })
            .Build();

        var sut = new FileReferenceHelper(configuration, NullLogger<FileReferenceHelper>.Instance);

        var reference = sut.Create(AccessHashType.Document, DocumentId);

        sut.Validate(reference, AccessHashType.Document, DocumentId).ShouldBe(FileReferenceState.Valid);
    }

    /// <summary>
    /// An unset or misspelled mode must not silently mean "no checking" — that is the state this whole
    /// mechanism exists to leave — nor "enforce", which would refuse every reference minted before the
    /// migration ran.
    /// </summary>
    [Fact]
    public void Mode_DefaultsToLogOnly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:AccessHashSecretKey"] = "test-secret-key",
                ["App:FileReferences:Mode"] = "nonsense"
            })
            .Build();

        new FileReferenceHelper(configuration, NullLogger<FileReferenceHelper>.Instance)
            .Mode.ShouldBe(FileReferenceMode.LogOnly);
    }

    private static byte[] Tampered(FileReferenceHelper sut)
    {
        var reference = sut.Create(AccessHashType.Document, DocumentId);
        reference[^1] ^= 0x01;
        return reference;
    }

    private static byte[] LongExpired(FileReferenceHelper sut)
    {
        return sut.CreateAt(AccessHashType.Document, DocumentId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2L * TtlSeconds);
    }

    private static string ThrownBy(Action action)
    {
        return Should.Throw<RpcException>(action).RpcError.Message;
    }

    private static long ReadIssuedAt(byte[] reference)
    {
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reference.AsSpan(0, 4));
    }

    private static FileReferenceHelper CreateSut(
        string secret = "test-secret-key",
        FileReferenceMode mode = FileReferenceMode.Enforce,
        int ttlHours = TtlHours)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FileReferences:SecretKey"] = secret,
                ["App:FileReferences:Mode"] = mode.ToString(),
                ["App:FileReferences:TtlHours"] = ttlHours.ToString()
            })
            .Build();

        return new FileReferenceHelper(configuration, NullLogger<FileReferenceHelper>.Instance);
    }
}
