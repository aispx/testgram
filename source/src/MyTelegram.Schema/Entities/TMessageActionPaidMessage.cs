namespace MyTelegram.Schema;

/// <summary>
/// A paid message was sent.
/// messageActionPaidMessage#5cd2501f stars:long = MessageAction;
/// </summary>
[TlObject(0x5cd2501f)]
public sealed partial class TMessageActionPaidMessage : IMessageAction
{
    public uint ConstructorId => 0x5cd2501f;

    public long Stars { get; set; }

    public void ComputeFlag() { }

    public void Serialize(IBufferWriter<byte> writer)
    {
        writer.Write(ConstructorId);
        writer.Write(Stars);
    }

    public void Deserialize(ref ReadOnlyMemory<byte> buffer)
    {
        Stars = buffer.ReadInt64();
    }
}
