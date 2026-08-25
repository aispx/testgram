using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MyTelegram.Messenger.Services.WebFiles;

/// <summary>
/// Signs the URLs this server hands out inside a <c>webDocument</c>, and checks the signature when one
/// comes back through <c>upload.getWebFile</c>.
///
/// <para>A proxied web document is quoted back verbatim by the client — <c>inputWebFileLocation</c>
/// carries the URL and the access hash it was given. Without a signature that would be an open HTTP
/// proxy: anything could be fetched through this server, including its own internal addresses. The hash
/// is therefore an HMAC of the URL under the server's own secret, so only a URL this server issued can
/// be asked for.</para>
/// See https://corefork.telegram.org/type/InputWebFileLocation
/// </summary>
public interface IWebDocumentUrlSigner
{
    long Sign(string url);

    bool IsSignatureValid(string url, long accessHash);
}

/// <inheritdoc />
public class WebDocumentUrlSigner(IConfiguration configuration) : IWebDocumentUrlSigner, ISingletonDependency
{
    private byte[]? _secret;

    public long Sign(string url)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(GetSecret(), Encoding.UTF8.GetBytes(url ?? string.Empty), hash);

        // The top bit is cleared so the value survives clients that treat it as a signed count, the same
        // way the other access hashes in this codebase are kept positive.
        return BitConverter.ToInt64(hash) & long.MaxValue;
    }

    public bool IsSignatureValid(string url, long accessHash)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        var expected = Sign(url);

        return CryptographicOperations.FixedTimeEquals(BitConverter.GetBytes(expected),
            BitConverter.GetBytes(accessHash));
    }

    private byte[] GetSecret()
    {
        if (_secret != null)
        {
            return _secret;
        }

        var masterKey = configuration.GetValue<string>("App:AccessHashSecretKey");
        if (string.IsNullOrEmpty(masterKey))
        {
            throw new InvalidOperationException("App:AccessHashSecretKey is null");
        }

        return _secret = Encoding.UTF8.GetBytes(masterKey);
    }
}

/// <summary>
/// Decides whether a web document goes out proxied, and signs it when it does.
///
/// <para>Proxying is only honest for a URL the file server has been told about: <c>upload.getWebFile</c>
/// is answered by the file server, which only serves a web file it has registered and downloaded. A
/// signed URL it would then refuse leaves the client with media it cannot read at all, while
/// <c>webDocumentNoProxy</c> at least lets it try the URL itself.</para>
/// </summary>
public interface IWebDocumentProxy
{
    bool CanProxy(string? url);

    long Sign(string url);
}

/// <inheritdoc />
public class WebDocumentProxy(IWebDocumentUrlSigner signer, IWebFileRegistrar registrar)
    : IWebDocumentProxy, ISingletonDependency
{
    public bool CanProxy(string? url)
    {
        return registrar.IsRegistered(url);
    }

    public long Sign(string url)
    {
        return signer.Sign(url);
    }
}
