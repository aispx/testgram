namespace MyTelegram.MTProto;

public interface IClientData
{
    public long AuthKeyId { get; set; }

    public string ConnectionId { get; set; }
    public int CurrentPacketLength { get; set; }

    public bool IsFirstPacketParsed { get; set; }
    public ProtocolType MtProtoType { get; set; }
    public bool ObfuscationEnabled { get; set; }
    public byte[] ReceiveKey { get; set; }
    public byte[] SendKey { get; set; }
    public int SkipCount { get; set; }

    public byte[] SendIv { get; set; }
    public byte[] ReceiveIv { get; set; }

    public ulong ReceiveCount { get; set; }
    public ulong SendCount { get; set; }

    /// <summary>
    ///     True when the frame currently being parsed had the quick-ack bit set in its transport
    ///     envelope (the MSB of the length field). See
    ///     https://corefork.telegram.org/mtproto/mtproto-transports#quick-ack.
    ///     <para>
    ///     The ack token itself - the first 4 bytes of the SHA256 that also yields msg_key, with
    ///     the top bit set, byte-swapped for the abridged transport - is deliberately NOT computed
    ///     here: it requires the auth key and the decrypted payload, and this repository's gateway
    ///     never decrypts (it forwards [auth_key_id][msg_key][ciphertext] onward). Emitting the
    ///     token is the session layer's job. This flag exists so the bit is recognised rather than
    ///     misread as part of the length, and as the extension point if that layer ever lands here.
    ///     </para>
    /// </summary>
    public bool QuickAckRequested { get; set; }
}

public interface IClientData<T> : IClientData
{
    T Data { get; set; }
}
