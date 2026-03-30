namespace MyTelegram.Messenger.Services.Passkey;

public class PasskeyDocument
{
    public string Id { get; set; } = null!; // credential id (base64url)
    public long UserId { get; set; }
    public string Name { get; set; } = null!;
    public byte[] PublicKey { get; set; } = null!; // COSE key bytes
    public uint SignCount { get; set; }
    public int CreatedAt { get; set; }
    public long? SoftwareEmojiId { get; set; }
    public int? LastUsageDate { get; set; }
}
