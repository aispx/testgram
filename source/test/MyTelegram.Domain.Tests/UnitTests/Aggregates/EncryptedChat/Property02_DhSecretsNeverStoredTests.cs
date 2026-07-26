using System.Reflection;
using MyTelegram.Domain.Aggregates.EncryptedChat;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats, Property 2: DH secrets are never stored.
///
/// For any created/accepted/discarded secret chat, the stored domain state exposes only g_a, g_b,
/// key_fingerprint and opaque blobs — never a private DH exponent or the shared DH key.
///
/// Validates: Requirements 16.2.
///
/// This is a structural assertion over the fields of <see cref="EncryptedChatState"/>: the whitelist of
/// stored fields is fixed, and no field name hints at a private exponent or a shared/derived key.
/// </summary>
public class Property02_DhSecretsNeverStoredTests
{
    private static readonly HashSet<string> AllowedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChatId",
        "AccessHash",
        "AdminId",
        "ParticipantId",
        "AdminPermAuthKeyId",
        "ParticipantPermAuthKeyId",
        "Ga",
        "Gb",
        "KeyFingerprint",
        "RandomId",
        "Date",
        "State",
        "HistoryDeleted",
        "SpamReporters"
    };

    private static readonly string[] ForbiddenSubstrings =
    {
        "private", "secret", "exponent", "sharedkey", "authkeybytes", "dhkey", "aeskey", "msgkey"
    };

    [Fact]
    public void EncryptedChatState_only_stores_public_dh_values_and_opaque_blobs()
    {
        var properties = typeof(EncryptedChatState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            AllowedFieldNames.ShouldContain(property.Name,
                $"Unexpected stored field '{property.Name}' — DH secrets must never be stored.");

            foreach (var forbidden in ForbiddenSubstrings)
            {
                property.Name.ToLowerInvariant().ShouldNotContain(forbidden);
            }
        }

        // The public DH material that IS allowed must be present.
        properties.ShouldContain(p => p.Name == "Ga");
        properties.ShouldContain(p => p.Name == "Gb");
        properties.ShouldContain(p => p.Name == "KeyFingerprint");
    }

    [Fact]
    public void Backing_fields_do_not_leak_private_dh_material()
    {
        var fields = typeof(EncryptedChatState)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        foreach (var field in fields)
        {
            foreach (var forbidden in ForbiddenSubstrings)
            {
                field.Name.ToLowerInvariant().ShouldNotContain(forbidden);
            }
        }
    }
}
