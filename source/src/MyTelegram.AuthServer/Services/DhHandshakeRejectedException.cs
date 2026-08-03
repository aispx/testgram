namespace MyTelegram.AuthServer.Services;

/// <summary>
///     Thrown when a client-supplied value in the auth key handshake fails one of the checks mandated by
///     https://corefork.telegram.org/mtproto/security_guidelines.
///     Distinguished from a plain <see cref="ArgumentException" /> so that the handshake can be rejected
///     deliberately rather than dropped as an unexpected failure.
/// </summary>
public class DhHandshakeRejectedException(string message) : Exception(message);
