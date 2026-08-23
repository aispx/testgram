using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: Telegram Passport authorization scopes.
///
/// <para>
/// A service encodes the documents it wants in the <c>scope</c> query parameter of its
/// <c>tg://passport</c> link, and the client passes that string on to
/// <c>account.getAuthorizationForm</c> untouched. The server turns it into the
/// <c>SecureRequiredType</c> tree the form is built from.
/// See https://corefork.telegram.org/api/passport#uripassportscope
/// </para>
/// </summary>
public class PassportScopeParserTests
{
    /// <summary>The example scope from the documentation page, with every feature exercised at once.</summary>
    private const string DocumentedScope =
        """
        {"v":1,"d":[{"_":"pd","n":1},"ad","pn","em",{"_":[{"_":"pp","s":1,"t":1},"ip","dl","ic"]},{"_":["ub","bs","ra","pr","tr"]}]}
        """;

    [Fact]
    public void The_documented_scope_parses_into_the_expected_tree()
    {
        var types = PassportScopeParser.Parse(DocumentedScope);

        types.Count.ShouldBe(6);

        types[0].ShouldBeOfType<TSecureRequiredType>().Type.ShouldBeOfType<TSecureValueTypePersonalDetails>();
        types[1].ShouldBeOfType<TSecureRequiredType>().Type.ShouldBeOfType<TSecureValueTypeAddress>();
        types[2].ShouldBeOfType<TSecureRequiredType>().Type.ShouldBeOfType<TSecureValueTypePhone>();
        types[3].ShouldBeOfType<TSecureRequiredType>().Type.ShouldBeOfType<TSecureValueTypeEmail>();

        var idDocuments = types[4].ShouldBeOfType<TSecureRequiredTypeOneOf>();
        idDocuments.Types.Count.ShouldBe(4);

        var addressDocuments = types[5].ShouldBeOfType<TSecureRequiredTypeOneOf>();
        addressDocuments.Types.Count.ShouldBe(5);
    }

    [Fact]
    public void The_native_names_flag_is_carried_over()
    {
        var types = PassportScopeParser.Parse(DocumentedScope);

        types[0].ShouldBeOfType<TSecureRequiredType>().NativeNames.ShouldBeTrue();
    }

    [Fact]
    public void The_selfie_and_translation_flags_of_a_group_apply_to_the_document_that_carries_them()
    {
        var types = PassportScopeParser.Parse(DocumentedScope);

        var passport = types[4].ShouldBeOfType<TSecureRequiredTypeOneOf>()
            .Types.OfType<TSecureRequiredType>()
            .Single(t => t.Type is TSecureValueTypePassport);

        passport.SelfieRequired.ShouldBeTrue();
        passport.TranslationRequired.ShouldBeTrue();
    }

    [Fact]
    public void A_flag_a_type_cannot_hold_is_dropped()
    {
        // A utility bill has no selfie field, so asking for one would make the client show an upload
        // slot the value cannot store.
        var types = PassportScopeParser.Parse("""{"v":1,"d":[{"_":"ub","s":1,"t":1}]}""");

        var utilityBill = types[0].ShouldBeOfType<TSecureRequiredType>();
        utilityBill.SelfieRequired.ShouldBeFalse();
        utilityBill.TranslationRequired.ShouldBeTrue();
    }

    [Fact]
    public void A_bare_string_element_needs_no_flags()
    {
        var types = PassportScopeParser.Parse("""{"v":1,"d":["pd"]}""");

        var personalDetails = types[0].ShouldBeOfType<TSecureRequiredType>();
        personalDetails.NativeNames.ShouldBeFalse();
        personalDetails.SelfieRequired.ShouldBeFalse();
        personalDetails.TranslationRequired.ShouldBeFalse();
    }

    [Fact]
    public void The_idd_alias_expands_to_a_one_of_group()
    {
        var types = PassportScopeParser.Parse("""{"v":1,"d":["idd"]}""");

        var group = types[0].ShouldBeOfType<TSecureRequiredTypeOneOf>();
        group.Types.Count.ShouldBe(3);
        group.Types.OfType<TSecureRequiredType>().Select(t => t.Type.ConstructorId)
            .ShouldBe([0x3dac6a00u, 0x6e425c4u, 0xa0d0744bu], ignoreOrder: true);
    }

    [Fact]
    public void The_add_alias_expands_to_a_one_of_group()
    {
        var types = PassportScopeParser.Parse("""{"v":1,"d":["add"]}""");

        types[0].ShouldBeOfType<TSecureRequiredTypeOneOf>().Types.Count.ShouldBe(3);
    }

    [Fact]
    public void A_type_used_twice_is_only_requested_once()
    {
        // "each type may be used only once in the entire array of UriPassportScopeElement objects" -
        // a duplicate would otherwise make the client render the same upload slot twice.
        var types = PassportScopeParser.Parse("""{"v":1,"d":["pp",{"_":["pp","dl"]}]}""");

        types.Count.ShouldBe(2);
        types[0].ShouldBeOfType<TSecureRequiredType>().Type.ShouldBeOfType<TSecureValueTypePassport>();
        types[1].ShouldBeOfType<TSecureRequiredType>().Type.ShouldBeOfType<TSecureValueTypeDriverLicense>();
    }

    [Fact]
    public void An_empty_scope_is_TYPES_EMPTY()
    {
        Should.Throw<RpcException>(() => PassportScopeParser.Parse("")).RpcError.Message.ShouldBe("TYPES_EMPTY");
        Should.Throw<RpcException>(() => PassportScopeParser.Parse("""{"v":1,"d":[]}"""))
            .RpcError.Message.ShouldBe("TYPES_EMPTY");
    }

    [Fact]
    public void A_scope_of_the_wrong_version_is_DATA_JSON_INVALID()
    {
        Should.Throw<RpcException>(() => PassportScopeParser.Parse("""{"v":2,"d":["pd"]}"""))
            .RpcError.Message.ShouldBe("DATA_JSON_INVALID");
    }

    [Fact]
    public void An_unparsable_scope_is_DATA_JSON_INVALID()
    {
        Should.Throw<RpcException>(() => PassportScopeParser.Parse("not json"))
            .RpcError.Message.ShouldBe("DATA_JSON_INVALID");
    }

    [Fact]
    public void An_unknown_document_type_is_DATA_JSON_INVALID()
    {
        Should.Throw<RpcException>(() => PassportScopeParser.Parse("""{"v":1,"d":["xx"]}"""))
            .RpcError.Message.ShouldBe("DATA_JSON_INVALID");
    }
}
