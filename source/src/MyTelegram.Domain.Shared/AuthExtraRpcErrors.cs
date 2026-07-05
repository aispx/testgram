namespace MyTelegram;

/// <summary>
/// Hand-written RPC errors for the <c>auth</c> namespace that are not present in the
/// generated <see cref="RpcErrors"/> (<c>RpcErrors.g.cs</c>). Do not add these to the
/// generated file; it is regenerated and would lose manual edits.
/// </summary>
public static class AuthExtraRpcErrors
{
    /// <summary>
    /// The login email is not installed for the identified App_Code.
    /// <code>
    /// auth.resendCode
    /// auth.resetLoginEmail
    /// </code>
    /// </summary>
    public static readonly RpcError EmailInstallMissing = new(400, "EMAIL_INSTALL_MISSING");
}
