namespace MyTelegram.Services.Services;

/// <summary>
/// Stamps a current <c>file_reference</c> onto the <c>document</c> and <c>photo</c> objects of an
/// outgoing response or update. See https://corefork.telegram.org/api/file-references
/// </summary>
public interface IFileReferenceStamper
{
    void Stamp(object? root);
}
