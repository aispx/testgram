using System.Buffers.Binary;

namespace MyTelegram.MTProto.Tests;

/// <summary>
///     A malformed frame must be refused at the point the length is read, not carried into a Slice.
///     https://corefork.telegram.org/mtproto/security_guidelines - "on no account is [the receiver] to access
///     data past the end of the decryption buffer".
/// </summary>
public class ParserBoundsTest
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    public void UnencryptedMessage_WithMessageDataLengthThatCannotHoldAConstructorId_IsRejected(
        int messageDataLength)
    {
        var frame = BuildUnencryptedFrame(messageDataLength, payloadLength: 16);

        Should.Throw<InvalidOperationException>(() => new UnencryptedMessageParser().Parse(frame));
    }

    [Fact]
    public void UnencryptedMessage_WithMessageDataLengthPastTheEndOfTheFrame_IsRejected()
    {
        // 16 bytes of payload actually present, 4096 declared.
        var frame = BuildUnencryptedFrame(messageDataLength: 4096, payloadLength: 16);

        Should.Throw<InvalidOperationException>(() => new UnencryptedMessageParser().Parse(frame));
    }

    [Fact]
    public void UnencryptedMessage_ShorterThanItsHeader_IsRejected()
    {
        Should.Throw<InvalidOperationException>(() => new UnencryptedMessageParser().Parse(new byte[19]));
    }

    [Fact]
    public void UnencryptedMessage_WithAConsistentLength_IsParsed()
    {
        var frame = BuildUnencryptedFrame(messageDataLength: 16, payloadLength: 16);

        var message = new UnencryptedMessageParser().Parse(frame);

        message.AuthKeyId.ShouldBe(0);
        message.MessageDataLength.ShouldBe(16);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(39)]
    public void EncryptedMessage_ShorterThanAuthKeyIdPlusMsgKeyPlusOneBlock_IsRejected(int length)
    {
        Should.Throw<InvalidOperationException>(() => new EncryptedMessageParser().Parse(new byte[length]));
    }

    [Fact]
    public void EncryptedMessage_AtTheMinimumLength_IsParsed()
    {
        var message = new EncryptedMessageParser().Parse(new byte[40]);

        message.MsgKey.Length.ShouldBe(16);
        message.EncryptedData.Length.ShouldBe(16);
    }

    private static byte[] BuildUnencryptedFrame(int messageDataLength, int payloadLength)
    {
        var frame = new byte[20 + payloadLength];
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(0, 8), 0);   // auth_key_id
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(8, 8), 1);   // message_id
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16, 4), messageDataLength);

        return frame;
    }
}
