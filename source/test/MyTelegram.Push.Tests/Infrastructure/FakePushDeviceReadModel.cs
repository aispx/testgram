using MyTelegram.ReadModel;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// A mutable, fully-constructible <see cref="IPushDeviceReadModel"/> test double. The production
/// <c>PushDeviceReadModel</c> exposes only private setters (it is populated by applying domain
/// events), so generators build this fake instead to produce arbitrary device fixtures (overlapping
/// tokens, multi-account <c>OtherUids</c>, every token type, etc.).
/// </summary>
public sealed class FakePushDeviceReadModel : IPushDeviceReadModel
{
    public bool AppSandbox { get; set; }
    public long PermAuthKeyId { get; set; }
    public string Id { get; set; } = string.Empty;
    public bool NoMuted { get; set; }
    public IReadOnlyList<long>? OtherUids { get; set; }
    public byte[]? Secret { get; set; }
    public string Token { get; set; } = string.Empty;
    public int TokenType { get; set; }
    public long UserId { get; set; }
}
