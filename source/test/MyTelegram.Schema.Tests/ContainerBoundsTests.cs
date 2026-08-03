using MyTelegram.Schema;

namespace MyTelegram.Schema.Tests;

/// <summary>
///     msg_container and its entries carry attacker-controlled lengths. They must be validated against the
///     remaining buffer before anything is allocated or sliced.
///     https://corefork.telegram.org/mtproto/security_guidelines
/// </summary>
public class ContainerBoundsTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000)]
    public void MsgContainer_DeclaringMoreEntriesThanTheBufferCouldHold_IsRejectedWithoutAllocating(int count)
    {
        var payload = WriteInt32(count);

        Should.Throw<InvalidOperationException>(() =>
        {
            ReadOnlyMemory<byte> local = payload;
            new TMsgContainer().Deserialize(ref local);
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MsgContainer_WithANegativeEntryCount_IsRejected(int count)
    {
        var payload = WriteInt32(count);

        Should.Throw<InvalidOperationException>(() =>
        {
            ReadOnlyMemory<byte> local = payload;
            new TMsgContainer().Deserialize(ref local);
        });
    }

    [Fact]
    public void MsgContainer_WithZeroEntries_IsAccepted()
    {
        var payload = WriteInt32(0);
        ReadOnlyMemory<byte> local = payload;

        new TMsgContainer().Deserialize(ref local);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ContainerMessage_WithABodyLengthThatDoesNotFitTheBuffer_IsRejected(int declaredBytes)
    {
        // msg_id(8) + seqno(4) + bytes(4) and no body at all.
        var payload = new byte[16];
        BitConverter.GetBytes(1L).CopyTo(payload, 0);
        BitConverter.GetBytes(1).CopyTo(payload, 8);
        BitConverter.GetBytes(declaredBytes).CopyTo(payload, 12);

        Should.Throw<InvalidOperationException>(() =>
        {
            ReadOnlyMemory<byte> local = payload;
            new TContainerMessage().Deserialize(ref local);
        });
    }

    private static byte[] WriteInt32(int value)
    {
        return BitConverter.GetBytes(value);
    }
}
