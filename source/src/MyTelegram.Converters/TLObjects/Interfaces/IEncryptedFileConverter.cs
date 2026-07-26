namespace MyTelegram.Converters.TLObjects.Interfaces;

public interface IEncryptedFileConverter : ILayeredConverter
{
    /// <summary>
    /// Null descriptor maps to <see cref="TEncryptedFileEmpty"/> (encryptedMessage.file is non-optional in TL).
    /// </summary>
    IEncryptedFile ToEncryptedFile(EncryptedFileDescriptor? descriptor);
}
