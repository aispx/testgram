using System.Text.Json;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Parses the <c>UriPassportScope</c> JSON a service puts in its <c>tg://passport</c> link into the
/// <c>SecureRequiredType</c> tree <c>account.authorizationForm</c> carries.
/// See https://corefork.telegram.org/api/passport#uripassportscope
/// </summary>
public static class PassportScopeParser
{
    /// <summary>
    /// Throws DATA_JSON_INVALID when the scope is not a well-formed v1 scope, TYPES_EMPTY when it asks
    /// for nothing.
    /// </summary>
    public static TVector<ISecureRequiredType> Parse(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(scope!);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            return null!;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1
            || !root.TryGetProperty("d", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            return null!;
        }

        var result = new TVector<ISecureRequiredType>();
        // "each type may be used only once in the entire array of UriPassportScopeElement objects"
        var seen = new HashSet<uint>();

        foreach (var element in elements.EnumerateArray())
        {
            var parsed = ParseElement(element, seen);
            if (parsed != null)
            {
                result.Add(parsed);
            }
        }

        if (result.Count == 0)
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
        }

        return result;
    }

    private static ISecureRequiredType? ParseElement(JsonElement element, HashSet<uint> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return ParseOne(element.GetString(), selfie: false, translation: false, nativeNames: false, seen);

            case JsonValueKind.Object:
            {
                if (!element.TryGetProperty("_", out var inner))
                {
                    RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
                }

                var selfie = GetFlag(element, "s");
                var translation = GetFlag(element, "t");
                var nativeNames = GetFlag(element, "n");

                if (inner.ValueKind == JsonValueKind.String)
                {
                    return ParseOne(inner.GetString(), selfie, translation, nativeNames, seen);
                }

                if (inner.ValueKind != JsonValueKind.Array)
                {
                    RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
                }

                // UriPassportScopeElementOneOfSeveral: the s/t flags on the group apply to whichever
                // document the user ends up picking, so they are set on every member.
                var types = new TVector<ISecureRequiredType>();
                foreach (var member in inner.EnumerateArray())
                {
                    var parsed = member.ValueKind switch
                    {
                        JsonValueKind.String => ParseOne(member.GetString(), selfie, translation, nativeNames, seen),
                        JsonValueKind.Object => ParseGroupMember(member, selfie, translation, nativeNames, seen),
                        _ => throw ThrowInvalid()
                    };

                    if (parsed != null)
                    {
                        Flatten(parsed, types);
                    }
                }

                if (types.Count == 0)
                {
                    return null;
                }

                return types.Count == 1 ? types[0] : new TSecureRequiredTypeOneOf { Types = types };
            }

            default:
                RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
                return null;
        }
    }

    private static ISecureRequiredType? ParseGroupMember(JsonElement member,
        bool groupSelfie,
        bool groupTranslation,
        bool groupNativeNames,
        HashSet<uint> seen)
    {
        if (!member.TryGetProperty("_", out var inner) || inner.ValueKind != JsonValueKind.String)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }

        return ParseOne(inner.GetString(),
            groupSelfie || GetFlag(member, "s"),
            groupTranslation || GetFlag(member, "t"),
            groupNativeNames || GetFlag(member, "n"),
            seen);
    }

    private static ISecureRequiredType? ParseOne(string? alias,
        bool selfie,
        bool translation,
        bool nativeNames,
        HashSet<uint> seen)
    {
        if (string.IsNullOrEmpty(alias))
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }

        // "idd" / "add" stand for one of a fixed group of documents.
        if (PassportValueTypes.GroupAliases.TryGetValue(alias!, out var group))
        {
            var types = new TVector<ISecureRequiredType>();
            foreach (var member in group)
            {
                var parsed = ParseOne(member, selfie, translation, nativeNames, seen);
                if (parsed != null)
                {
                    types.Add(parsed);
                }
            }

            return types.Count switch
            {
                0 => null,
                1 => types[0],
                _ => new TSecureRequiredTypeOneOf { Types = types }
            };
        }

        if (!PassportValueTypes.TryGetConstructorId(alias!, out var constructorId))
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }

        if (!seen.Add(constructorId))
        {
            return null;
        }

        var type = PassportValueTypes.Create(constructorId)!;

        return new TSecureRequiredType
        {
            Type = type,
            // A selfie/translation/native-names flag is only meaningful on the types that support it;
            // setting it elsewhere would make the client ask for a field the type cannot hold.
            SelfieRequired = selfie && PassportValueTypes.SelfieCapable.Contains(alias!),
            TranslationRequired = translation && PassportValueTypes.TranslationCapable.Contains(alias!),
            NativeNames = nativeNames && alias == PassportValueTypes.PersonalDetails
        };
    }

    /// <summary>Nested one-of groups (an "idd" inside a group) are folded into the outer group.</summary>
    private static void Flatten(ISecureRequiredType type, TVector<ISecureRequiredType> destination)
    {
        if (type is TSecureRequiredTypeOneOf oneOf)
        {
            foreach (var inner in oneOf.Types)
            {
                Flatten(inner, destination);
            }

            return;
        }

        destination.Add(type);
    }

    private static bool GetFlag(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    private static Exception ThrowInvalid()
    {
        RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        return new InvalidOperationException();
    }
}
