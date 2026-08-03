namespace MyTelegram.AuthServer.Services;

public record Step3Output(
    long AuthKeyId,
    byte[] AuthKey,
    long ServerSalt,
    bool IsPermanent,
    ISetClientDHParamsAnswer SetClientDhParamsAnswer,
    int? DcId = null,
    /// <summary>
    ///     When <c>true</c> the handshake failed a security check: <see cref="SetClientDhParamsAnswer" /> carries a
    ///     <c>dh_gen_fail</c> and the key material must neither be cached nor published.
    /// </summary>
    bool Rejected = false
);