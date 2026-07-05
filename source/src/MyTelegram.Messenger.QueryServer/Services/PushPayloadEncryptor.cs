using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyTelegram.Core;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Builds and encrypts PUSH-notification payloads in the MTProto-encrypted format expected by
/// official clients (see https://corefork.telegram.org/api/push-updates).
/// <para>
/// The client registers a 256-byte <c>secret</c> (push auth key) with <c>account.registerDevice</c>.
/// The server then encrypts each notification's JSON payload as
/// <c>[auth_key_id(8)][msg_key(16)][aes_ige(payload)]</c> where the payload itself is
/// <c>[int32_le len][json bytes][random padding to 16]</c>. The client reconstructs the exact
/// same keys (msg_key from SHA256(secret[96..128] + payload), aes key/iv v2 derivation with x=8),
/// matching <c>MessageKeyData.generateMessageKeyData</c> / <c>PushListenerController</c> in the
/// Android client.
/// </para>
/// </summary>
public static class PushPayloadEncryptor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null
    };

    /// <summary>
    /// Serializes <paramref name="data"/> to the compact JSON the clients expect
    /// (<c>{"loc_key","loc_args","user_id","custom":{...},"sound"}</c>).
    /// </summary>
    public static string BuildJson(PushData data)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("loc_key", data.LocKey);
            if (data.LocArgs is { Length: > 0 })
            {
                writer.WritePropertyName("loc_args");
                writer.WriteStartArray();
                foreach (var arg in data.LocArgs)
                {
                    writer.WriteStringValue(arg);
                }
                writer.WriteEndArray();
            }
            if (data.UserId != 0)
            {
                writer.WriteNumber("user_id", data.UserId);
            }
            if (data.Custom is { } custom)
            {
                writer.WritePropertyName("custom");
                writer.WriteStartObject();
                WriteCustom(writer, custom);
                writer.WriteEndObject();
            }
            if (!string.IsNullOrEmpty(data.Sound))
            {
                writer.WriteString("sound", data.Sound);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCustom(Utf8JsonWriter writer, PushNotificationCustomData c)
    {
        // Only non-null fields are emitted; numeric ids are written as JSON numbers so the
        // Android client's custom.getLong/getInt helpers work without string parsing.
        if (c.Attachb64 is { } b64) writer.WriteString("attachb64", b64);
        if (c.Updates is { } updates) writer.WriteString("updates", updates);
        if (c.CallId.HasValue) writer.WriteNumber("call_id", c.CallId.Value);
        if (c.CallAh.HasValue) writer.WriteNumber("call_ah", c.CallAh.Value);
        if (c.EncryptionId.HasValue) writer.WriteNumber("encryption_id", c.EncryptionId.Value);
        if (c.RandomId.HasValue) writer.WriteNumber("random_id", c.RandomId.Value);
        if (c.ContactId.HasValue) writer.WriteNumber("contact_id", c.ContactId.Value);
        if (c.MsgId.HasValue) writer.WriteNumber("msg_id", c.MsgId.Value);
        if (c.ChannelId.HasValue) writer.WriteNumber("channel_id", c.ChannelId.Value);
        if (c.ChatId.HasValue) writer.WriteNumber("chat_id", c.ChatId.Value);
        if (c.FromId.HasValue) writer.WriteNumber("from_id", c.FromId.Value);
        if (c.ChatFromBroadcastId.HasValue) writer.WriteNumber("chat_from_broadcast_id", c.ChatFromBroadcastId.Value);
        if (c.ChatFromGroupId.HasValue) writer.WriteNumber("chat_from_group_id", c.ChatFromGroupId.Value);
        if (c.ChatFromId.HasValue) writer.WriteNumber("chat_from_id", c.ChatFromId.Value);
        if (c.Mention.HasValue) writer.WriteNumber("mention", c.Mention.Value ? 1 : 0);
        if (c.Silent.HasValue) writer.WriteNumber("silent", c.Silent.Value ? 1 : 0);
        if (c.Schedule.HasValue) writer.WriteNumber("schedule", c.Schedule.Value ? 1 : 0);
        if (c.EditDate.HasValue) writer.WriteNumber("edit_date", c.EditDate.Value);
        if (c.TopMsgId.HasValue) writer.WriteNumber("top_msg_id", c.TopMsgId.Value);
        if (c.Messages is { } msgs) writer.WriteString("messages", msgs);
        if (c.MaxId.HasValue) writer.WriteNumber("max_id", c.MaxId.Value);
        if (c.ReportDeliveryUntilDate.HasValue) writer.WriteNumber("report_delivery_until_date", c.ReportDeliveryUntilDate.Value);
        if (c.Dc.HasValue) writer.WriteNumber("dc", c.Dc.Value);
        if (c.Addr is { } addr) writer.WriteString("addr", addr);
    }

    /// <summary>
    /// Builds the MTProto push payload (the content that is then AES-IGE encrypted) for a device.
    /// Returns the base64url-encoded wire format ready for the provider "p"/data field.
    /// When <paramref name="secret"/> is null/empty, returns base64url of the plaintext JSON so
    /// that legacy clients without a push secret still receive a usable (unencrypted) payload.
    /// </summary>
    public static string EncryptForDevice(byte[]? secret, PushData data, IMtpHelper mtpHelper, IAuthKeyIdHelper authKeyIdHelper)
    {
        var json = BuildJson(data);
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        if (secret is null || secret.Length == 0)
        {
            // Unencrypted fallback (older clients / web push body).
            return Base64UrlEncode(jsonBytes);
        }

        // payload = [int32_le len][json][random padding to multiple of 16]
        var withLen = new byte[4 + jsonBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(withLen, jsonBytes.Length);
        jsonBytes.CopyTo(withLen, 4);

        var remainder = withLen.Length % 16;
        var padding = remainder == 0 ? 16 : (16 - remainder);
        var padded = new byte[withLen.Length + padding];
        Buffer.BlockCopy(withLen, 0, padded, 0, withLen.Length);
        RandomNumberGenerator.Fill(padded.AsSpan(withLen.Length)); // random padding

        var authKeyId = authKeyIdHelper.GetAuthKeyId(secret);

        // output = [authKeyId:8][msgKey:16][aesIge(padded)]
        var output = new byte[24 + padded.Length];
        mtpHelper.Encrypt(authKeyId, secret, padded, output);

        return Base64UrlEncode(output);
    }

    /// <summary>base64url encoding without padding (used by official clients for the "p" field).</summary>
    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
