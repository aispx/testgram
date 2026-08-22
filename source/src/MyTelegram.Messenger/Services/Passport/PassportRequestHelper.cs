namespace MyTelegram.Messenger.Services.Passport;

/// <summary>Shared request-side validation for the <c>account.*SecureValue*</c> methods.</summary>
public static class PassportRequestHelper
{
    /// <summary>Length of every hash Telegram Passport works with (SHA-256).</summary>
    public const int HashLength = 32;

    /// <summary>
    /// Turns a <c>Vector&lt;SecureValueType&gt;</c> into constructor ids, rejecting an empty vector with
    /// TYPES_EMPTY and an unknown constructor with DATA_JSON_INVALID. Duplicates are collapsed.
    /// </summary>
    public static List<uint> ToConstructorIds(TVector<ISecureValueType>? types)
    {
        if (types == null || types.Count == 0)
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
        }

        var result = new List<uint>();
        foreach (var type in types!)
        {
            if (!PassportValueTypes.IsKnown(type.ConstructorId))
            {
                RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            }

            if (!result.Contains(type.ConstructorId))
            {
                result.Add(type.ConstructorId);
            }
        }

        return result;
    }

    /// <summary>Checks which of the <c>inputSecureValue</c> fields the given type is allowed to carry.</summary>
    public static void EnsureFieldsAllowed(TInputSecureValue value)
    {
        var type = value.Type.ConstructorId;
        if (!PassportValueTypes.IsKnown(type))
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }

        var allowed = PassportValueTypes.GetAllowedFields(type);
        var present = PassportValueFields.None;

        if (value.Data != null) present |= PassportValueFields.Data;
        if (value.FrontSide != null) present |= PassportValueFields.FrontSide;
        if (value.ReverseSide != null) present |= PassportValueFields.ReverseSide;
        if (value.Selfie != null) present |= PassportValueFields.Selfie;
        if (value.Files is { Count: > 0 }) present |= PassportValueFields.Files;
        if (value.Translation is { Count: > 0 }) present |= PassportValueFields.Translation;
        if (value.PlainData != null) present |= PassportValueFields.PlainData;

        if (present == PassportValueFields.None || (present & ~allowed) != PassportValueFields.None)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }
    }
}
