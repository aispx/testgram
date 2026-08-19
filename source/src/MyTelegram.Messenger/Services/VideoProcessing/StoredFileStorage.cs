using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.VideoProcessing;

/// <summary>
/// Reads and writes the raw file objects the file server keeps in the object store.
/// </summary>
/// <remarks>
/// Files live in the MinIO bucket under their file id, and a body is AES-CTR encrypted whenever the
/// file server recorded a key for it (uploads from clients); server generated files, like the ones
/// created for sticker sets, are stored as-is and carry no key.
/// </remarks>
public interface IStoredFileStorage
{
    /// <summary>
    /// Downloads a stored file into <paramref name="destinationPath"/>, decrypting it when needed.
    /// Returns false when the object does not exist.
    /// </summary>
    Task<bool> DownloadToFileAsync(long fileId, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a file body under <paramref name="fileId"/>, without encryption.
    /// </summary>
    Task UploadFileAsync(long fileId, string sourcePath, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class StoredFileStorage(
    IConfiguration configuration,
    IMongoDatabase mongoDatabase,
    ILogger<StoredFileStorage> logger)
    : IStoredFileStorage, ITransientDependency
{
    private const string Region = "us-east-1";
    private const int BufferSize = 1 << 20;

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    private string Endpoint
    {
        get
        {
            var endpoint = configuration["Minio:Endpoint"] ?? "minio:9000";
            if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = $"http://{endpoint}";
            }

            return endpoint.TrimEnd('/');
        }
    }

    private string Bucket => configuration["Minio:BucketName"] ?? "tg-files";
    private string AccessKey => configuration["Minio:AccessKey"] ?? string.Empty;
    private string SecretKey => configuration["Minio:SecretKey"] ?? string.Empty;

    public async Task<bool> DownloadToFileAsync(long fileId, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/{Bucket}/{Uri.EscapeDataString(fileId.ToString(CultureInfo.InvariantCulture))}";
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("File {FileId} is not in the object store ({StatusCode})", fileId, response.StatusCode);
            return false;
        }

        var (key, iv) = await LoadEncryptionKeyAsync(fileId);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath, BufferSize);

        if (key == null || iv == null)
        {
            await source.CopyToAsync(destination, cancellationToken);
            return true;
        }

        await DecryptAsync(source, destination, key, iv, cancellationToken);
        return true;
    }

    public async Task UploadFileAsync(long fileId, string sourcePath, CancellationToken cancellationToken = default)
    {
        var objectName = fileId.ToString(CultureInfo.InvariantCulture);
        var payloadHash = await ComputeSha256Async(sourcePath, cancellationToken);

        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = new Uri(Endpoint).Authority;
        var canonicalUri = $"/{Bucket}/{Uri.EscapeDataString(objectName)}";

        // Minimal AWS Signature Version 4 for a single PUT: MinIO rejects anonymous writes, and the
        // gRPC media service caps a message at 4 MB, which no real video rendition fits into.
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = $"PUT\n{canonicalUri}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{dateStamp}/{Region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes($"AWS4{SecretKey}"), dateStamp), Region), "s3"),
            "aws4_request");
        var signature = Hex(Hmac(signingKey, stringToSign));

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Endpoint}{canonicalUri}");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");

        await using var stream = File.OpenRead(sourcePath);
        request.Content = new StreamContent(stream, BufferSize);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = stream.Length;

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Could not store file {fileId} in the object store: {(int)response.StatusCode} {body}");
        }
    }

    private async Task<(byte[]? Key, byte[]? Iv)> LoadEncryptionKeyAsync(long fileId)
    {
        var document = await mongoDatabase.GetCollection<BsonDocument>("eventflow-filereadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("FileId", fileId))
            .FirstOrDefaultAsync();

        if (document == null ||
            !document.TryGetValue("Key", out var key) || !key.IsBsonBinaryData ||
            !document.TryGetValue("Iv", out var iv) || !iv.IsBsonBinaryData)
        {
            return (null, null);
        }

        return (key.AsBsonBinaryData.Bytes, iv.AsBsonBinaryData.Bytes);
    }

    private static async Task DecryptAsync(Stream source, Stream destination, byte[] key, byte[] iv,
        CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        var counter = new byte[16];
        iv.CopyTo(counter, 0);
        var counterBlock = new byte[16];
        var keyStream = new byte[16];
        var buffer = new byte[BufferSize];

        int read;
        // Whole AES blocks per pass: a short read in the middle of the stream would otherwise restart
        // the keystream half a block early and corrupt everything after it.
        while ((read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false,
                   cancellationToken)) > 0)
        {
            for (var offset = 0; offset < read; offset += 16)
            {
                counter.CopyTo(counterBlock, 0);
                encryptor.TransformBlock(counterBlock, 0, counterBlock.Length, keyStream, 0);

                var count = Math.Min(16, read - offset);
                for (var i = 0; i < count; i++)
                {
                    buffer[offset + i] ^= keyStream[i];
                }

                IncrementCounter(counter);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Hex(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
