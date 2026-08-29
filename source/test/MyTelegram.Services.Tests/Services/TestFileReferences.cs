using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests;

/// <summary>
/// A real <see cref="FileReferenceHelper"/> over an in-memory configuration, for converters that mint a
/// <c>file_reference</c> as a side effect of the thing actually under test.
/// See https://corefork.telegram.org/api/file-references
/// </summary>
public static class TestFileReferences
{
    public static IFileReferenceHelper Helper { get; } = Create();

    public static FileReferenceHelper Create(string secret = "test-secret-key")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FileReferences:SecretKey"] = secret
            })
            .Build();

        return new FileReferenceHelper(configuration, NullLogger<FileReferenceHelper>.Instance);
    }
}
