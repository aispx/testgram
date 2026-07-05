using System.Reflection;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests;

public class DcIdNormalizerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Normalize_FixesBusinessIntroStickerUnknownDcId(int badDcId)
    {
        var document = new TDocument
        {
            Id = 123,
            AccessHash = 456,
            FileReference = ReadOnlyMemory<byte>.Empty,
            Date = 0,
            MimeType = string.Empty,
            Size = 0,
            DcId = badDcId,
            Attributes = []
        };
        var userFull = new TUserFull
        {
            Id = 1,
            Settings = new TPeerSettings(),
            NotifySettings = new TPeerNotifySettings(),
            BusinessIntro = new TBusinessIntro
            {
                Title = "intro",
                Description = "description",
                Sticker = document
            }
        };

        InvokeNormalizer(userFull);

        document.DcId.ShouldBe(2);
    }

    [Fact]
    public void Normalize_KeepsAdvertisedDcId()
    {
        var document = new TDocument
        {
            Id = 123,
            AccessHash = 456,
            FileReference = ReadOnlyMemory<byte>.Empty,
            Date = 0,
            MimeType = string.Empty,
            Size = 0,
            DcId = 5,
            Attributes = []
        };

        InvokeNormalizer(document);

        document.DcId.ShouldBe(5);
    }

    private static void InvokeNormalizer(object root)
    {
        var normalizerType = typeof(RpcResultObjectHandler<,>).Assembly.GetType(
            "MyTelegram.Services.Services.DcIdNormalizer",
            throwOnError: true)!;
        var method = normalizerType.GetMethod(
            "Normalize",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        method.Invoke(null, [root, nameof(DcIdNormalizerTests)]);
    }
}
