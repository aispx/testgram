using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Converters.TLObjects.LatestLayer;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats, Property 20: Result serialization completeness.
///
/// For every TL result object a secret-chat handler returns (encryptedChatWaiting, encryptedChat,
/// encryptedChatRequested, encryptedChatDiscarded, sentEncryptedMessage, sentEncryptedFile, encryptedFile,
/// encryptedFileEmpty, TVector&lt;long&gt;, boolTrue) built from any valid content, serialization succeeds
/// with no missing-required-field error.
///
/// Validates: Requirements 17.3, 17.4.
/// </summary>
public class Property20_ResultSerializationTests
{
    private readonly EncryptedChatConverter _chatConverter = new();
    private readonly EncryptedFileConverter _fileConverter = new();
    private readonly EncryptedMessageConverter _messageConverter = new();

    [Property(MaxTest = 100)]
    public void EncryptedChat_results_serialize(long chatId, long accessHash, int date, long adminId,
        long participantId, long keyFingerprint)
    {
        AssertSerializes(_chatConverter.ToEncryptedChatWaiting(chatId, accessHash, date, adminId, participantId));
        AssertSerializes(_chatConverter.ToEncryptedChatRequested(chatId, accessHash, date, adminId, participantId,
            ga: [1, 2, 3]));
        AssertSerializes(_chatConverter.ToEncryptedChat(chatId, accessHash, date, adminId, participantId,
            gaOrB: [4, 5, 6], keyFingerprint));
        AssertSerializes(_chatConverter.ToEncryptedChatDiscarded(chatId, historyDeleted: true));
        AssertSerializes(_chatConverter.ToEncryptedChatDiscarded(chatId, historyDeleted: false));
    }

    [Property(MaxTest = 100)]
    public void EncryptedFile_results_serialize(long id, long accessHash, long size, int dcId, int keyFingerprint)
    {
        AssertSerializes(_fileConverter.ToEncryptedFile(new EncryptedFileDescriptor(id, accessHash, size, dcId,
            keyFingerprint)));
        AssertSerializes(_fileConverter.ToEncryptedFile(descriptor: null)); // encryptedFileEmpty
    }

    [Property(MaxTest = 100)]
    public void SentEncrypted_and_scalar_results_serialize(int date, long[] randomIds)
    {
        AssertSerializes(new TSentEncryptedMessage { Date = date });
        AssertSerializes(new TSentEncryptedFile { Date = date, File = new TEncryptedFileEmpty() });
        AssertSerializes(new TSentEncryptedFile
        {
            Date = date,
            File = new TEncryptedFile { Id = 1, AccessHash = 2, Size = 3, DcId = 1, KeyFingerprint = 4 }
        });
        AssertSerializes(new TVector<long>(randomIds ?? []));
        AssertSerializes(new TBoolTrue());
    }

    [Property(MaxTest = 100)]
    public void Update_results_serialize(int chatId, int qts, int maxDate, int date)
    {
        var waiting = _chatConverter.ToEncryptedChatWaiting(chatId, 1, date, 10, 20);
        AssertSerializes(_chatConverter.ToUpdateEncryption(waiting, date));

        var encMessage = new TEncryptedMessage
        {
            RandomId = 1,
            ChatId = chatId,
            Date = date,
            Bytes = new byte[] { 1, 2, 3 },
            File = new TEncryptedFileEmpty()
        };
        AssertSerializes(_messageConverter.ToUpdateNewEncryptedMessage(encMessage, qts));
        AssertSerializes(_messageConverter.ToUpdateEncryptedChatTyping(chatId));
        AssertSerializes(_messageConverter.ToUpdateEncryptedMessagesRead(chatId, maxDate, date));
    }

    private static void AssertSerializes(IObject result)
    {
        var bytes = Should.NotThrow(() => result.ToBytes());
        bytes.ShouldNotBeNull();
        bytes!.Length.ShouldBeGreaterThan(0);
    }
}
