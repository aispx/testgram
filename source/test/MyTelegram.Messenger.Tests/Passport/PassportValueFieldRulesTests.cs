using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: which fields an <c>inputSecureValue</c> of a given type may carry.
///
/// <para>
/// The table is normative — see "Here's a list of possible SecureValueTypes, and the parameters that
/// can be set/requested when using each type" on https://corefork.telegram.org/api/passport . A value
/// carrying a field its type does not have would be stored but never rendered, and the bot would
/// receive a document it cannot interpret.
/// </para>
/// </summary>
public class PassportValueFieldRulesTests
{
    private static readonly ISecureData Data = new TSecureData
    {
        Data = new byte[] { 1 }, DataHash = new byte[32], Secret = new byte[32]
    };

    private static readonly IInputSecureFile File = new TInputSecureFile { Id = 1, AccessHash = 2 };

    [Theory]
    [InlineData(0x9d2a81e3u)] // personalDetails
    [InlineData(0xcbe31e26u)] // address
    public void Data_only_types_accept_data(uint type)
    {
        Should.NotThrow(() => PassportRequestHelper.EnsureFieldsAllowed(Value(type, data: Data)));
        ShouldReject(Value(type, frontSide: File));
        ShouldReject(Value(type, files: [File]));
        ShouldReject(Value(type, plainData: new TSecurePlainPhone { Phone = "1" }));
    }

    [Theory]
    [InlineData(0x3dac6a00u)] // passport
    [InlineData(0x99a48f23u)] // internalPassport
    public void A_one_sided_id_document_has_no_reverse_side(uint type)
    {
        Should.NotThrow(() => PassportRequestHelper.EnsureFieldsAllowed(
            Value(type, data: Data, frontSide: File, selfie: File, translation: [File])));

        ShouldReject(Value(type, data: Data, reverseSide: File));
    }

    [Theory]
    [InlineData(0x6e425c4u)] // driverLicense
    [InlineData(0xa0d0744bu)] // identityCard
    public void A_two_sided_id_document_accepts_a_reverse_side(uint type)
    {
        Should.NotThrow(() => PassportRequestHelper.EnsureFieldsAllowed(
            Value(type, data: Data, frontSide: File, reverseSide: File, selfie: File, translation: [File])));

        ShouldReject(Value(type, data: Data, files: [File]));
    }

    [Theory]
    [InlineData(0xfc36954eu)] // utilityBill
    [InlineData(0x89137c0du)] // bankStatement
    [InlineData(0x8b883488u)] // rentalAgreement
    [InlineData(0x99e3806au)] // passportRegistration
    [InlineData(0xea02ec33u)] // temporaryRegistration
    public void A_scan_type_accepts_files_and_translations_only(uint type)
    {
        Should.NotThrow(() => PassportRequestHelper.EnsureFieldsAllowed(
            Value(type, files: [File], translation: [File])));

        ShouldReject(Value(type, data: Data));
        ShouldReject(Value(type, files: [File], selfie: File));
    }

    [Theory]
    [InlineData(0xb320aadbu)] // phone
    [InlineData(0x8e3ca7eeu)] // email
    public void A_plain_type_accepts_plain_data_only(uint type)
    {
        Should.NotThrow(() => PassportRequestHelper.EnsureFieldsAllowed(
            Value(type, plainData: new TSecurePlainEmail { Email = "a@b.c" })));

        ShouldReject(Value(type, data: Data));
    }

    [Fact]
    public void A_value_with_no_field_at_all_is_rejected()
    {
        ShouldReject(Value(0x9d2a81e3u));
    }

    [Fact]
    public void An_unknown_type_is_rejected()
    {
        ShouldReject(Value(0xdeadbeef, data: Data));
    }

    private static void ShouldReject(TInputSecureValue value)
    {
        Should.Throw<RpcException>(() => PassportRequestHelper.EnsureFieldsAllowed(value))
            .RpcError.Message.ShouldBe("DATA_JSON_INVALID");
    }

    private static TInputSecureValue Value(uint type,
        ISecureData? data = null,
        IInputSecureFile? frontSide = null,
        IInputSecureFile? reverseSide = null,
        IInputSecureFile? selfie = null,
        IInputSecureFile[]? files = null,
        IInputSecureFile[]? translation = null,
        ISecurePlainData? plainData = null)
    {
        return new TInputSecureValue
        {
            Type = PassportValueTypes.Create(type) ?? new UnknownSecureValueType(type),
            Data = data,
            FrontSide = frontSide,
            ReverseSide = reverseSide,
            Selfie = selfie,
            Files = files == null ? null : new TVector<IInputSecureFile>(files),
            Translation = translation == null ? null : new TVector<IInputSecureFile>(translation),
            PlainData = plainData
        };
    }

    /// <summary>Stands in for a constructor the server does not know, which cannot be built from the schema.</summary>
    private sealed class UnknownSecureValueType(uint constructorId) : ISecureValueType
    {
        public uint ConstructorId { get; } = constructorId;

        public void Serialize(System.Buffers.IBufferWriter<byte> writer) => throw new NotSupportedException();

        public void Deserialize(ref ReadOnlyMemory<byte> buffer) => throw new NotSupportedException();
    }
}
