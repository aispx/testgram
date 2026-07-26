using System.Reflection;
using EventFlow.Aggregates;
using MyTelegram.Domain.Aggregates.EncryptedChat;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats — shared helpers for the EncryptedChat aggregate property tests.
/// </summary>
internal static class EncryptedChatTestHelper
{
    public static EncryptedChatAggregate NewAggregate(int chatId)
    {
        return new EncryptedChatAggregate(EncryptedChatId.Create(chatId));
    }

    /// <summary>Reads the aggregate's private EncryptedChatState via reflection.</summary>
    public static EncryptedChatState GetState(EncryptedChatAggregate aggregate)
    {
        var field = typeof(EncryptedChatAggregate).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (EncryptedChatState)field.GetValue(aggregate)!;
    }

    public static ChatState GetChatState(EncryptedChatAggregate aggregate)
    {
        return GetState(aggregate).State;
    }
}
