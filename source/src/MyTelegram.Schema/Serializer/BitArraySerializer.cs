namespace MyTelegram.Schema.Serializer;

public class BitArraySerializer : ISerializer<BitArray>
{
    public void Serialize(BitArray value,
        IBufferWriter<byte> writer)
    {
        var data = new byte[(value.Length - 1) / 8 + 1];
        value.CopyTo(data, 0);
        writer.WriteRawBytes(data);
    }

    public BitArray Deserialize(ref ReadOnlyMemory<byte> buffer)
    {
        // Copy only the 4 flag bytes. buffer.CopyTo(data) copies the *whole* remaining buffer and throws
        // "Destination is too short" whenever anything follows the flags.
        var data = new byte[4];
        buffer[..4].CopyTo(data);
        var value = new BitArray(data);
        buffer = buffer[4..];

        return value;
    }
}