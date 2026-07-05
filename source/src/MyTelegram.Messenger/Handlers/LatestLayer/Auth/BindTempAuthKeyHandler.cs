using MyTelegram.Domain.Aggregates.Device;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Binds a temporary authorization key <code>temp_auth_key_id</code> to the permanent authorization key <code>perm_auth_key_id</code>. Each permanent key may only be bound to one temporary key at a time, binding a new temporary key overwrites the previous one.For more information, see <a href="https://corefork.telegram.org/api/pfs">Perfect Forward Secrecy</a>.
/// Possible errors
///   
/// nonce long Random long
/// temp_auth_key_id long Temporary auth_key_id
/// perm_auth_key_id long Permanent auth_key_id to bind to
/// temp_session_id long Session id, which will be used to invoke <strong>auth.bindTempAuthKey</strong> method
/// expires_at int Unix timestamp to invalidate temporary key
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.bindTempAuthKey"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✔]
/// </remarks>
internal sealed class BindTempAuthKeyHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestBindTempAuthKey, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Auth.RequestBindTempAuthKey obj)
    {
        // Structural validation of the (already-decrypted) binding message payload. Full
        // cryptographic decryption of the perm-key-encrypted inner message is a transport/gateway
        // concern; here we validate that EncryptedMessage is non-empty and parses as a
        // TBindAuthKeyInner whose ids/nonce are consistent with the request. (Requirement 3.5)
        TBindAuthKeyInner? inner = null;
        if (obj.EncryptedMessage.Length > 0)
        {
            try
            {
                inner = obj.EncryptedMessage.ToTObject<TBindAuthKeyInner>();
            }
            catch
            {
                inner = null;
            }
        }

        if (inner == null
            || inner.TempAuthKeyId != input.AuthKeyId
            || inner.PermAuthKeyId != obj.PermAuthKeyId
            || inner.Nonce != obj.Nonce)
        {
            RpcErrors.RpcErrors400.EncryptedMessageInvalid.ThrowRpcError();
        }

        // Perm-key existence: the perm key must correspond to an existing session/device.
        // (Requirement 3.4)
        var device = await queryProcessor.ProcessAsync(new GetDeviceByAuthKeyIdQuery(obj.PermAuthKeyId));
        if (device == null)
        {
            RpcErrors.RpcErrors401.AuthKeyPermEmpty.ThrowRpcError();
        }

        // Record/refresh the binding, overwriting any prior temp binding and storing the expiry.
        // (Requirements 3.1, 3.2, 3.3)
        await commandBus.PublishAsync(new BindTempAuthKeyCommand(
            DeviceId.Create(obj.PermAuthKeyId),
            obj.PermAuthKeyId,
            input.AuthKeyId,
            obj.ExpiresAt));

        return new TBoolTrue();
    }
}
