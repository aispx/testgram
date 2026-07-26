namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class EncryptedFileConverter : IEncryptedFileConverter, ITransientDependency
{
    public virtual int Layer => Layers.LayerLatest;

    public virtual IEncryptedFile ToEncryptedFile(EncryptedFileDescriptor? descriptor)
    {
        if (descriptor == null)
        {
            return new TEncryptedFileEmpty();
        }

        return new TEncryptedFile
        {
            Id = descriptor.Id,
            AccessHash = descriptor.AccessHash,
            Size = descriptor.Size,
            DcId = descriptor.DcId,
            KeyFingerprint = descriptor.KeyFingerprint
        };
    }
}
