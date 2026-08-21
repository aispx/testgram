namespace MyTelegram.Services.Services;

/// <summary>
/// Validates every <c>input*FromMessage</c> constructor an incoming request carries, before the
/// request handler runs.
/// <para>
/// A peer that a client only ever saw through a <a href="https://corefork.telegram.org/api/min">min
/// constructor</a> has no usable access hash, so the client cites the context it was seen in — a
/// container peer plus a message id — through <c>inputPeerUserFromMessage</c>,
/// <c>inputPeerChannelFromMessage</c>, <c>inputUserFromMessage</c> or
/// <c>inputChannelFromMessage</c>. Because those constructors carry no access hash, the usual
/// access-hash check has nothing to verify and every field but the cited context is attacker
/// chosen: without validating the context they are a way to address any peer id at all.
/// </para>
/// <para>
/// Validating them per handler leaves the check to whoever remembers it, so it runs here for every
/// request instead. See https://corefork.telegram.org/api/peers#access-hash
/// </para>
/// </summary>
public interface IFromMessageContextValidator
{
    /// <summary>
    /// Walks <paramref name="request"/> and validates the context of every <c>input*FromMessage</c>
    /// constructor reachable from it.
    /// </summary>
    /// <exception cref="RpcException">
    /// When any cited context does not hold up: the caller is a bot, the message is not one they can
    /// read, or the peer they name does not appear in it.
    /// </exception>
    Task ValidateAsync(IRequestInput input, IObject request);
}
