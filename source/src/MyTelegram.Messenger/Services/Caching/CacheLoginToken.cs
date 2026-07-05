namespace MyTelegram.Messenger.Services.Caching;

public record CacheLoginToken(long AuthKeyId,
    long UserId,
    byte[] Token)
{
    public static string GetTokenKey(byte[] token) => BitConverter.ToString(token);

    public static string GetTokenKey(ReadOnlyMemory<byte> token) => BitConverter.ToString(token.ToArray());
}
