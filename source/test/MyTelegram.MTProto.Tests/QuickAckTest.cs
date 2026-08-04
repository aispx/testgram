using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Abstractions;

namespace MyTelegram.MTProto.Tests;

/// <summary>
///     Quick acknowledgment: the client raises the MSB of the transport envelope's length field to
///     ask for one. The parser must strip that bit and still deliver the frame - reading the
///     intermediate length as a signed int32 makes it negative, which used to be rejected as a
///     malformed length and tore the connection down.
///     https://corefork.telegram.org/mtproto/mtproto-transports#quick-ack
/// </summary>
public class QuickAckTest
{
    private const uint QuickAckMask = 0x80000000;
    private const int PayloadLength = 40;

    private static MtpMessageParser CreateParser()
    {
        return new MtpMessageParser(
            NullLogger<MtpMessageParser>.Instance,
            new UnencryptedMessageParser(),
            new EncryptedMessageParser(),
            new FirstPacketParser(NullLogger<FirstPacketParser>.Instance, new AesHelper()),
            new AesHelper());
    }

    private static IClientData CreateClientData(ProtocolType protocolType)
    {
        return new ClientData
        {
            MtProtoType = protocolType,
            IsFirstPacketParsed = true,
            ObfuscationEnabled = false,
            ConnectionId = "test"
        };
    }

    /// <summary>Encrypted frame: auth_key_id(8) + msg_key(16) + at least one 16-byte block.</summary>
    private static byte[] CreateEncryptedPayload()
    {
        var payload = new byte[PayloadLength];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), 12345);

        return payload;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IntermediateFrame_WithAndWithoutTheQuickAckBit_IsParsed(bool quickAck)
    {
        var payload = CreateEncryptedPayload();
        var frame = new byte[4 + payload.Length];
        var length = (uint)payload.Length;
        if (quickAck)
        {
            length |= QuickAckMask;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), length);
        payload.CopyTo(frame.AsSpan(4));

        var clientData = CreateClientData(ProtocolType.Intermediate);
        var buffer = new ReadOnlySequence<byte>(frame);

        CreateParser().TryParse(ref buffer, clientData, out var message).ShouldBeTrue();

        message.ShouldBeOfType<EncryptedMessage>().AuthKeyId.ShouldBe(12345);
        clientData.QuickAckRequested.ShouldBe(quickAck);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbridgedFrame_WithAndWithoutTheQuickAckBit_IsParsed(bool quickAck)
    {
        var payload = CreateEncryptedPayload();

        // Abridged encodes the length as payload/4 in a single byte; quick-ack adds 0x80.
        var lengthByte = (byte)(payload.Length / 4);
        if (quickAck)
        {
            lengthByte |= 0x80;
        }

        var frame = new byte[1 + payload.Length];
        frame[0] = lengthByte;
        payload.CopyTo(frame.AsSpan(1));

        var clientData = CreateClientData(ProtocolType.Abridge);
        var buffer = new ReadOnlySequence<byte>(frame);

        CreateParser().TryParse(ref buffer, clientData, out var message).ShouldBeTrue();

        message.ShouldBeOfType<EncryptedMessage>().AuthKeyId.ShouldBe(12345);
        clientData.QuickAckRequested.ShouldBe(quickAck);
    }

    /// <summary>
    ///     Stripping the quick-ack bit must not weaken the bounds check: a length that is still
    ///     nonsense after masking has to be refused.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(1024u * 1024 * 11)]
    public void IntermediateFrame_WithAnOutOfRangeLength_IsStillRejectedWhenQuickAckIsRequested(uint length)
    {
        var frame = new byte[4 + PayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), length | QuickAckMask);

        var clientData = CreateClientData(ProtocolType.Intermediate);
        var buffer = new ReadOnlySequence<byte>(frame);

        Should.Throw<InvalidOperationException>(() =>
        {
            var b = buffer;
            CreateParser().TryParse(ref b, clientData, out _);
        });
    }
}
