using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MyTelegram.Messenger.Handlers.LatestLayer.Upload;
using MyTelegram.Messenger.Services.WebFiles;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.WebFiles;

/// <summary>
/// Feature: <a href="https://core.telegram.org/method/upload.getWebFile">upload.getWebFile</a>, the read
/// side of a proxied <c>webDocument</c>.
///
/// <para>
/// It exists here for GIF search. Telegram clients only render inline media that arrives as a
/// <c>webDocument</c> — Android's <c>ContextLinkCell</c> tests <c>instanceof TL_webDocument</c> and
/// <c>TL_webDocumentNoProxy</c> is a sibling class, not a subclass — so a no-proxy result leaves the grid
/// tile empty however small the preview is. Proxying means this server fetches the bytes, which makes the
/// signature on the URL the thing standing between the method and an open HTTP proxy.
/// </para>
/// </summary>
public class WebFileProxyTests
{
    private const string Url = "https://media.tenor.com/abc/cat.mp4";

    [Fact]
    public void A_url_signature_is_stable_and_specific()
    {
        var signer = Signer();

        var hash = signer.Sign(Url);

        signer.Sign(Url).ShouldBe(hash);
        signer.Sign(Url + "?x=1").ShouldNotBe(hash);
        signer.IsSignatureValid(Url, hash).ShouldBeTrue();
        // Positive, like the other access hashes this server issues.
        hash.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_signature_from_another_secret_or_another_url_is_refused()
    {
        var signer = Signer();
        var other = Signer("a different secret");

        signer.IsSignatureValid(Url, other.Sign(Url)).ShouldBeFalse();
        signer.IsSignatureValid(Url, signer.Sign("https://media.tenor.com/abc/dog.mp4")).ShouldBeFalse();
        signer.IsSignatureValid(Url, 0).ShouldBeFalse();
        signer.IsSignatureValid(string.Empty, signer.Sign(string.Empty)).ShouldBeFalse();
    }

    /// <summary>
    /// The host list is the second fence: a signed URL is one this server issued, but the list keeps a
    /// bug elsewhere from turning into a request to an arbitrary address.
    /// </summary>
    [Theory]
    [InlineData("https://media.tenor.com/a/b.mp4", true)]
    [InlineData("https://tenor.com/a/b.mp4", true)]
    [InlineData("https://media1.tenor.com/a/b.mp4", true)]
    [InlineData("http://media.tenor.com/a/b.mp4", false)]
    [InlineData("https://nottenor.com/a/b.mp4", false)]
    [InlineData("https://tenor.com.evil.example/a/b.mp4", false)]
    [InlineData("https://127.0.0.1/a/b.mp4", false)]
    [InlineData("https://minio:9000/tg-files/x", false)]
    [InlineData("not a url", false)]
    public void Only_configured_hosts_are_fetched(string url, bool allowed)
    {
        Fetcher().IsAllowed(url).ShouldBe(allowed);
    }

    [Fact]
    public async Task A_slice_is_cut_from_the_body_and_the_full_size_is_reported()
    {
        var body = new byte[1000];
        Random.Shared.NextBytes(body);

        var file = await GetAsync(body, offset: 100, limit: 200);

        file.Size.ShouldBe(1000);
        file.Bytes.Length.ShouldBe(200);
        file.Bytes.ToArray().ShouldBe(body[100..300]);
        file.MimeType.ShouldBe("video/mp4");
        file.FileType.ShouldBeOfType<MyTelegram.Schema.Storage.TFileMp4>();
    }

    [Fact]
    public async Task A_slice_that_runs_past_the_end_is_truncated_rather_than_refused()
    {
        var body = new byte[300];

        var file = await GetAsync(body, offset: 200, limit: 512 * 1024);

        file.Size.ShouldBe(300);
        file.Bytes.Length.ShouldBe(100);
    }

    /// <summary>
    /// Reading past the end answers an empty slice: that is how a client learns it has the whole file,
    /// and an exception here would look like a broken download instead.
    /// </summary>
    [Fact]
    public async Task Reading_past_the_end_answers_nothing()
    {
        var file = await GetAsync(new byte[10], offset: 10, limit: 1024);

        file.Bytes.Length.ShouldBe(0);
        file.Size.ShouldBe(10);
    }

    [Fact]
    public async Task A_url_this_server_did_not_sign_is_refused()
    {
        var exception = await Should.ThrowAsync<RpcException>(() =>
            GetAsync(new byte[10], offset: 0, limit: 1024, accessHash: 12345));

        exception.RpcError.Message.ShouldBe(RpcErrors.RpcErrors400.LocationInvalid.Message);
    }

    [Fact]
    public async Task A_body_that_could_not_be_fetched_is_reported_as_unavailable()
    {
        var exception = await Should.ThrowAsync<RpcException>(() =>
            GetAsync(body: null, offset: 0, limit: 1024));

        exception.RpcError.Message.ShouldBe(RpcErrors.RpcErrors400.WebfileNotAvailable.Message);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(-1, 1024)]
    [InlineData(0, 0)]
    [InlineData(0, 2 * 1024 * 1024)]
    public async Task An_impossible_offset_or_limit_is_refused(int offset, int limit)
    {
        if (offset == 0 && limit == 1024)
        {
            // The valid combination, kept in the table so the boundaries around it are visible.
            (await GetAsync(new byte[10], offset, limit)).Size.ShouldBe(10);

            return;
        }

        await Should.ThrowAsync<RpcException>(() => GetAsync(new byte[10], offset, limit));
    }

    [Fact]
    public void A_mime_type_maps_onto_the_file_type_a_client_decodes_with()
    {
        WebFileTypeMapper.Map("video/mp4").ShouldBeOfType<MyTelegram.Schema.Storage.TFileMp4>();
        WebFileTypeMapper.Map("image/png").ShouldBeOfType<MyTelegram.Schema.Storage.TFilePng>();
        WebFileTypeMapper.Map("IMAGE/JPEG").ShouldBeOfType<MyTelegram.Schema.Storage.TFileJpeg>();
        WebFileTypeMapper.Map("image/gif").ShouldBeOfType<MyTelegram.Schema.Storage.TFileGif>();
        WebFileTypeMapper.Map("image/webp").ShouldBeOfType<MyTelegram.Schema.Storage.TFileWebp>();
        WebFileTypeMapper.Map(null).ShouldBeOfType<MyTelegram.Schema.Storage.TFileUnknown>();
        WebFileTypeMapper.Map("application/octet-stream")
            .ShouldBeOfType<MyTelegram.Schema.Storage.TFileUnknown>();
    }

    private static async Task<MyTelegram.Schema.Upload.TWebFile> GetAsync(byte[]? body, int offset, int limit,
        long? accessHash = null)
    {
        var signer = Signer();
        var fetcher = new Mock<IWebFileFetcher>(MockBehavior.Loose);
        fetcher.Setup(p => p.GetAsync(Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(body == null ? null : new WebFileBody(body, "video/mp4"));

        var handler = new GetWebFileHandler(signer, fetcher.Object,
            NullLogger<GetWebFileHandler>.Instance);

        var request = new MyTelegram.Schema.Upload.RequestGetWebFile
        {
            Location = new TInputWebFileLocation { Url = Url, AccessHash = accessHash ?? signer.Sign(Url) },
            Offset = offset,
            Limit = limit
        };

        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(2_000_001);

        var method = typeof(GetWebFileHandler).GetMethod("HandleCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            var task = (Task<MyTelegram.Schema.Upload.IWebFile>)method.Invoke(handler, [input.Object, request])!;

            return (MyTelegram.Schema.Upload.TWebFile)await task;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static WebDocumentUrlSigner Signer(string secret = "test-secret")
    {
        var configuration = new Mock<Microsoft.Extensions.Configuration.IConfiguration>(MockBehavior.Loose);
        var section = new Mock<Microsoft.Extensions.Configuration.IConfigurationSection>(MockBehavior.Loose);
        section.SetupGet(p => p.Value).Returns(secret);
        section.SetupGet(p => p.Path).Returns("App:AccessHashSecretKey");
        configuration.Setup(p => p.GetSection("App:AccessHashSecretKey")).Returns(section.Object);

        return new WebDocumentUrlSigner(configuration.Object);
    }

    private static WebFileFetcher Fetcher()
    {
        var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>(MockBehavior.Loose);
        options.SetupGet(p => p.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        return new WebFileFetcher(null!, options.Object,
            NullLogger<WebFileFetcher>.Instance);
    }
}
