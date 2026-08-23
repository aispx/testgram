namespace MyTelegram.Messenger.Services.Passport;

/// <summary>Fields a <c>secureValue</c> of a given type is allowed to carry.</summary>
[Flags]
public enum PassportValueFields
{
    None = 0,
    Data = 1 << 0,
    FrontSide = 1 << 1,
    ReverseSide = 1 << 2,
    Selfie = 1 << 3,
    Files = 1 << 4,
    PlainData = 1 << 5,
    Translation = 1 << 6
}

/// <summary>
/// The 13 <c>SecureValueType</c> constructors, their short scope aliases and the fields each of them
/// accepts. The allowed-field table is normative — see the "Here's a list of possible SecureValueTypes"
/// table on https://corefork.telegram.org/api/passport .
/// </summary>
public static class PassportValueTypes
{
    public const string PersonalDetails = "pd";
    public const string Passport = "pp";
    public const string DriverLicense = "dl";
    public const string IdentityCard = "ic";
    public const string InternalPassport = "ip";
    public const string Address = "ad";
    public const string UtilityBill = "ub";
    public const string BankStatement = "bs";
    public const string RentalAgreement = "ra";
    public const string PassportRegistration = "pr";
    public const string TemporaryRegistration = "tr";
    public const string PhoneNumber = "pn";
    public const string Email = "em";

    /// <summary>Alias for any one of "pp", "dl", "ic".</summary>
    public const string IdDocument = "idd";

    /// <summary>Alias for any one of "ub", "bs", "ra".</summary>
    public const string AddressDocument = "add";

    private const PassportValueFields IdDocumentFields =
        PassportValueFields.Data | PassportValueFields.FrontSide | PassportValueFields.Selfie |
        PassportValueFields.Translation;

    private const PassportValueFields TwoSidedIdDocumentFields =
        IdDocumentFields | PassportValueFields.ReverseSide;

    private const PassportValueFields ScanFields =
        PassportValueFields.Files | PassportValueFields.Translation;

    private static readonly IReadOnlyDictionary<string, uint> ConstructorIdByAlias = new Dictionary<string, uint>
    {
        [PersonalDetails] = 0x9d2a81e3,
        [Passport] = 0x3dac6a00,
        [DriverLicense] = 0x06e425c4,
        [IdentityCard] = 0xa0d0744b,
        [InternalPassport] = 0x99a48f23,
        [Address] = 0xcbe31e26,
        [UtilityBill] = 0xfc36954e,
        [BankStatement] = 0x89137c0d,
        [RentalAgreement] = 0x8b883488,
        [PassportRegistration] = 0x99e3806a,
        [TemporaryRegistration] = 0xea02ec33,
        [PhoneNumber] = 0xb320aadb,
        [Email] = 0x8e3ca7ee
    };

    private static readonly IReadOnlyDictionary<uint, string> AliasByConstructorId =
        ConstructorIdByAlias.ToDictionary(p => p.Value, p => p.Key);

    private static readonly IReadOnlyDictionary<uint, PassportValueFields> FieldsByConstructorId =
        new Dictionary<uint, PassportValueFields>
        {
            [ConstructorIdByAlias[PersonalDetails]] = PassportValueFields.Data,
            [ConstructorIdByAlias[Address]] = PassportValueFields.Data,
            [ConstructorIdByAlias[Passport]] = IdDocumentFields,
            [ConstructorIdByAlias[InternalPassport]] = IdDocumentFields,
            [ConstructorIdByAlias[DriverLicense]] = TwoSidedIdDocumentFields,
            [ConstructorIdByAlias[IdentityCard]] = TwoSidedIdDocumentFields,
            [ConstructorIdByAlias[UtilityBill]] = ScanFields,
            [ConstructorIdByAlias[BankStatement]] = ScanFields,
            [ConstructorIdByAlias[RentalAgreement]] = ScanFields,
            [ConstructorIdByAlias[PassportRegistration]] = ScanFields,
            [ConstructorIdByAlias[TemporaryRegistration]] = ScanFields,
            [ConstructorIdByAlias[PhoneNumber]] = PassportValueFields.PlainData,
            [ConstructorIdByAlias[Email]] = PassportValueFields.PlainData
        };

    /// <summary>The group aliases usable in a scope, expanded to the concrete types they stand for.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> GroupAliases = new Dictionary<string, string[]>
    {
        [IdDocument] = [Passport, DriverLicense, IdentityCard],
        [AddressDocument] = [UtilityBill, BankStatement, RentalAgreement]
    };

    /// <summary>Types for which a selfie may be requested.</summary>
    public static readonly IReadOnlySet<string> SelfieCapable =
        new HashSet<string> { Passport, DriverLicense, IdentityCard, InternalPassport };

    /// <summary>Types for which a translation may be requested.</summary>
    public static readonly IReadOnlySet<string> TranslationCapable =
        new HashSet<string>
        {
            Passport, DriverLicense, IdentityCard, InternalPassport,
            UtilityBill, BankStatement, RentalAgreement, PassportRegistration, TemporaryRegistration
        };

    public static bool TryGetConstructorId(string alias, out uint constructorId) =>
        ConstructorIdByAlias.TryGetValue(alias, out constructorId);

    public static string? GetAlias(uint constructorId) =>
        AliasByConstructorId.TryGetValue(constructorId, out var alias) ? alias : null;

    public static bool IsKnown(uint constructorId) => FieldsByConstructorId.ContainsKey(constructorId);

    public static PassportValueFields GetAllowedFields(uint constructorId) =>
        FieldsByConstructorId.TryGetValue(constructorId, out var fields) ? fields : PassportValueFields.None;

    /// <summary>Rebuilds the <c>SecureValueType</c> constructor from its id.</summary>
    public static ISecureValueType? Create(uint constructorId) => constructorId switch
    {
        0x9d2a81e3 => new TSecureValueTypePersonalDetails(),
        0x3dac6a00 => new TSecureValueTypePassport(),
        0x06e425c4 => new TSecureValueTypeDriverLicense(),
        0xa0d0744b => new TSecureValueTypeIdentityCard(),
        0x99a48f23 => new TSecureValueTypeInternalPassport(),
        0xcbe31e26 => new TSecureValueTypeAddress(),
        0xfc36954e => new TSecureValueTypeUtilityBill(),
        0x89137c0d => new TSecureValueTypeBankStatement(),
        0x8b883488 => new TSecureValueTypeRentalAgreement(),
        0x99e3806a => new TSecureValueTypePassportRegistration(),
        0xea02ec33 => new TSecureValueTypeTemporaryRegistration(),
        0xb320aadb => new TSecureValueTypePhone(),
        0x8e3ca7ee => new TSecureValueTypeEmail(),
        _ => null
    };
}
