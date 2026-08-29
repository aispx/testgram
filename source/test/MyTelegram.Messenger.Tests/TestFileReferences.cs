using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests;

/// <summary>
/// A real <see cref="FileReferenceHelper"/> over an in-memory configuration, for the many conversion
/// tests that need to mint a <c>file_reference</c> but have nothing to say about it.
/// See https://corefork.telegram.org/api/file-references
/// </summary>
public static class TestFileReferences
{
    /// <summary>
    /// The default mode is <see cref="FileReferenceMode.LogOnly"/>, so this helper mints real references
    /// but refuses nothing — which is what a conversion test wants.
    /// </summary>
    public static IFileReferenceHelper Helper { get; } = Create();

    /// <summary>For the tests that assert a reference is actually refused.</summary>
    public static IFileReferenceHelper Enforcing { get; } = Create(mode: FileReferenceMode.Enforce);

    public static FileReferenceHelper Create(string secret = "test-secret-key",
        FileReferenceMode mode = FileReferenceMode.LogOnly)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FileReferences:SecretKey"] = secret,
                ["App:FileReferences:Mode"] = mode.ToString()
            })
            .Build();

        return new FileReferenceHelper(configuration, NullLogger<FileReferenceHelper>.Instance);
    }
}
