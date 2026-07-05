using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FsCheck;
using MyTelegram.Core;
using MyTelegram.Messenger;
using MyTelegram.Schema;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// Central catalogue of FsCheck generators for the push-updates feature. Every later property test
/// composes these (directly or via <see cref="PushArbitraries"/>) so the input space — registration
/// requests, web-push tokens, payloads, secrets, message items, device sets, provider configs — is
/// defined and constrained in exactly one place.
/// </summary>
public static class PushGen
{
    // ---- Primitive id / scalar generators ----------------------------------------------------

    /// <summary>Positive 64-bit identifier (user id / auth key id), drawn from a small-ish range.</summary>
    public static Gen<long> PositiveId =>
        Gen.Choose(1, 1_000_000).Select(i => (long)i);

    /// <summary>Identifier drawn from a tiny pool so collisions/overlaps across devices are likely.</summary>
    public static Gen<long> PooledUserId =>
        Gen.Choose(1, 20).Select(i => (long)i);

    /// <summary>Supported token type (Requirement 1.3): one of {1,2,3,5,6,7,8,9,10,11,12,13}.</summary>
    public static Gen<int> SupportedTokenType =>
        Gen.Elements(PushTokenTypes.Supported.ToArray());

    /// <summary>Token type guaranteed to be OUTSIDE the supported set (e.g. 0, 4, 14, negatives).</summary>
    public static Gen<int> UnsupportedTokenType =>
        Gen.Choose(-5, 40).Where(i => !PushTokenTypes.IsSupported(i));

    /// <summary>Any token type, supported or not.</summary>
    public static Gen<int> AnyTokenType =>
        Gen.OneOf(SupportedTokenType, UnsupportedTokenType);

    /// <summary>A 256-byte push secret (auth key) used for MTProto v2 payload encryption.</summary>
    public static Gen<byte[]> Secret256 =>
        GenHelpers.ArrayOfLength(MtProtoV2ReferenceCrypto.SecretLength, Gen.Choose(0, 255).Select(i => (byte)i));

    /// <summary>
    /// A secret slot that is sometimes absent (null/empty) to exercise the plaintext fallback.
    /// Weighted ~50% real secret / ~25% empty / ~25% null by repeating the secret option.
    /// </summary>
    public static Gen<byte[]?> OptionalSecret =>
        Gen.OneOf(
            Secret256.Select(s => (byte[]?)s),
            Secret256.Select(s => (byte[]?)s),
            Gen.Constant((byte[]?)Array.Empty<byte>()),
            Gen.Constant((byte[]?)null));

    /// <summary>Non-empty opaque device token (alphanumeric).</summary>
    public static Gen<string> NonEmptyToken =>
        (from n in Gen.Choose(8, 24)
         from chars in GenHelpers.ArrayOfLength(n, Gen.Elements("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray()))
         select new string(chars));

    /// <summary>Empty or whitespace-only token (should be classified TOKEN_EMPTY).</summary>
    public static Gen<string> EmptyOrWhitespaceToken =>
        Gen.Elements("", " ", "  ", "\t", "\n", "   \t  ");

    /// <summary>Token drawn from a tiny pool so device sets contain duplicates.</summary>
    public static Gen<string> PooledToken =>
        Gen.Elements("token-A", "token-B", "token-C");

    /// <summary>List of OtherUids (0..4 entries) drawn from a small pool, so overlaps are common.</summary>
    public static Gen<List<long>> OtherUids =>
        (from n in Gen.Choose(0, 4)
         from ids in GenHelpers.ArrayOfLength(n, PooledUserId)
         select ids.ToList());

    // ---- Web-push (token_type 10) JSON tokens ------------------------------------------------

    /// <summary>A valid web-push JSON token (endpoint + base64url P-256 p256dh + base64url auth).</summary>
    public static Gen<string> ValidWebPushTokenJson =>
        Gen.Fresh(() => BuildWebPushJson(includeEndpoint: true, p256dh: ValidP256dh(), auth: ValidAuth()));

    /// <summary>A web-push token case tagged with the validation outcome it should produce.</summary>
    public static Gen<WebPushTokenCase> WebPushTokenCase =>
        Gen.OneOf(
            Gen.Fresh(() => new WebPushTokenCase(
                BuildWebPushJson(true, ValidP256dh(), ValidAuth()), WebPushTokenKind.Valid)),
            Gen.Fresh(() => new WebPushTokenCase(
                BuildWebPushJson(false, ValidP256dh(), ValidAuth()), WebPushTokenKind.MissingEndpoint)),
            Gen.Fresh(() => new WebPushTokenCase(
                BuildWebPushJson(true, ValidP256dh(), auth: null), WebPushTokenKind.MissingAuth)),
            Gen.Fresh(() => new WebPushTokenCase(
                BuildWebPushJson(true, ValidP256dh(), auth: "!!!not-base64url!!!"), WebPushTokenKind.InvalidAuth)),
            Gen.Fresh(() => new WebPushTokenCase(
                BuildWebPushJson(true, p256dh: null, auth: ValidAuth()), WebPushTokenKind.MissingKey)),
            Gen.Fresh(() => new WebPushTokenCase(
                // base64url of only 10 bytes => not a valid 65-byte P-256 point.
                BuildWebPushJson(true, p256dh: Base64UrlReference.Encode(RandomNumberGenerator.GetBytes(10)), auth: ValidAuth()),
                WebPushTokenKind.InvalidKey)));

    // ---- Registration requests ---------------------------------------------------------------

    /// <summary>A fully valid registration request (supported type, non-empty token, valid web-push when type 10).</summary>
    public static Gen<DeviceRegistration> ValidRegistration =>
        (from userId in PooledUserId
         from authKeyId in PositiveId
         from tokenType in SupportedTokenType
         from token in (tokenType == 10 ? ValidWebPushTokenJson : NonEmptyToken)
         from secret in OptionalSecret
         from noMuted in Arb.Generate<bool>()
         from appSandbox in Arb.Generate<bool>()
         from otherUids in OtherUids
         select new DeviceRegistration(userId, authKeyId, tokenType, token,
             secret ?? Array.Empty<byte>(), noMuted, appSandbox, otherUids));

    /// <summary>A registration case spanning all expected validity classifications.</summary>
    public static Gen<RegistrationCase> RegistrationCase =>
        Gen.OneOf(
            ValidRegistration.Select(r => new RegistrationCase(r, RegistrationValidity.Valid)),
            // TOKEN_EMPTY
            (from r in ValidRegistration
             from empty in EmptyOrWhitespaceToken
             select new RegistrationCase(r with { Token = empty }, RegistrationValidity.TokenEmpty)),
            // TOKEN_TYPE_INVALID
            (from r in ValidRegistration
             from bad in UnsupportedTokenType
             from token in NonEmptyToken
             select new RegistrationCase(r with { TokenType = bad, Token = token }, RegistrationValidity.TokenTypeInvalid)),
            // WEBPUSH_* (type 10 with a broken token)
            (from r in ValidRegistration
             from wp in WebPushTokenCase.Where(c => c.Kind != WebPushTokenKind.Valid)
             select new RegistrationCase(
                 r with { TokenType = 10, Token = wp.Json },
                 wp.Kind switch
                 {
                     WebPushTokenKind.MissingEndpoint => RegistrationValidity.WebPushTokenInvalid,
                     WebPushTokenKind.MissingAuth or WebPushTokenKind.InvalidAuth => RegistrationValidity.WebPushAuthInvalid,
                     _ => RegistrationValidity.WebPushKeyInvalid
                 })));

    // ---- Payload (PushData / PushNotificationCustomData) -------------------------------------

    /// <summary>All loc_key constants declared in <see cref="PushNotificationTypes"/>.</summary>
    public static readonly IReadOnlyList<string> AllLocKeys =
        typeof(PushNotificationTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

    /// <summary>A loc_key drawn from the known taxonomy.</summary>
    public static Gen<string> LocKey => Gen.Elements(AllLocKeys.ToArray());

    /// <summary>A custom-data block with a representative mix of populated fields.</summary>
    public static Gen<PushNotificationCustomData> CustomData =>
        (from msgId in Gen.Choose(1, 100000)
         from fromId in PooledUserId
         from silent in Arb.Generate<bool>()
         from mention in Arb.Generate<bool>()
         from withChannel in Arb.Generate<bool>()
         select new PushNotificationCustomData
         {
             MsgId = msgId,
             FromId = fromId,
             ChannelId = withChannel ? fromId + 1000 : null,
             Silent = silent,
             Mention = mention
         });

    /// <summary>A push payload with a taxonomy loc_key, recipient user id and custom block.</summary>
    public static Gen<PushData> PushData =>
        (from locKey in LocKey
         from userId in PooledUserId
         from arg1 in Gen.Elements("Alice", "Bob", "Carol", "Группа")
         from body in Gen.Elements("hello", "see attached", "", "длинный текст")
         from custom in CustomData
         from silent in Arb.Generate<bool>()
         select new PushData(locKey, new[] { arg1, body }, userId, custom, silent ? null : "default"));

    // ---- Peers & messages --------------------------------------------------------------------

    public static Gen<Peer> UserPeer => PooledUserId.Select(id => new Peer(PeerType.User, id));
    public static Gen<Peer> ChatPeer => PooledUserId.Select(id => new Peer(PeerType.Chat, id + 100));
    public static Gen<Peer> ChannelPeer => PooledUserId.Select(id => new Peer(PeerType.Channel, id + 1000));

    public static Gen<Peer> AnyPeer => Gen.OneOf(UserPeer, ChatPeer, ChannelPeer);

    private static readonly MessageType[] MediaMessageTypes =
    {
        MessageType.Photo, MessageType.Video, MessageType.Document, MessageType.Gif,
        MessageType.Voice, MessageType.Music, MessageType.Geo, MessageType.Game, MessageType.Poll
    };

    /// <summary>A message fixture covering text/media/reaction/call across User/Chat/Channel peers.</summary>
    public static Gen<MessageCase> MessageCase =>
        (from toPeer in AnyPeer
         from senderId in PooledUserId
         from msgId in Gen.Choose(1, 100000)
         from randomId in PositiveId
         from kind in Gen.Elements(MessageKind.Text, MessageKind.Media, MessageKind.Reaction, MessageKind.Call)
         from text in Gen.Elements("hi", "hello world", "", "сообщение")
         from mediaType in Gen.Elements(MediaMessageTypes)
         from emoji in Gen.Elements("👍", "❤", "🔥", "😢")
         select BuildMessageCase(toPeer, senderId, msgId, randomId, kind, text, mediaType, emoji));

    // ---- Devices & device sets ---------------------------------------------------------------

    /// <summary>A single device read-model fixture.</summary>
    public static Gen<FakePushDeviceReadModel> Device =>
        (from userId in PooledUserId
         from authKeyId in PositiveId
         from tokenType in SupportedTokenType
         from token in PooledToken
         from secret in OptionalSecret
         from noMuted in Arb.Generate<bool>()
         from appSandbox in Arb.Generate<bool>()
         from otherUids in OtherUids
         select new FakePushDeviceReadModel
         {
             Id = token,
             UserId = userId,
             PermAuthKeyId = authKeyId,
             TokenType = tokenType,
             Token = token,
             Secret = secret is { Length: > 0 } ? secret : null,
             NoMuted = noMuted,
             AppSandbox = appSandbox,
             OtherUids = otherUids
         });

    /// <summary>
    /// A set of devices for one recipient, deliberately built from small token/uid pools so it
    /// contains duplicate <c>Token</c>s and overlapping <c>OtherUids</c> (for dedup/multi-account tests).
    /// </summary>
    public static Gen<DeviceSet> DeviceSet =>
        (from recipient in PooledUserId
         from count in Gen.Choose(1, 6)
         from devices in GenHelpers.ArrayOfLength(count, Device)
         select new DeviceSet(devices.ToList(), recipient));

    // ---- Provider configuration --------------------------------------------------------------

    /// <summary>A provider configuration whose credentials are present or blank at random.</summary>
    public static Gen<ProviderConfigCase> ProviderConfig =>
        (from master in Arb.Generate<bool>()
         from fcmCreds in Arb.Generate<bool>()
         from apnsCreds in Arb.Generate<bool>()
         from webCreds in Arb.Generate<bool>()
         select new ProviderConfigCase(BuildPushConfig(master, fcmCreds, apnsCreds, webCreds)));

    // ---- builders ----------------------------------------------------------------------------

    private static MessageCase BuildMessageCase(Peer toPeer, long senderId, int msgId, long randomId,
        MessageKind kind, string text, MessageType mediaType, string emoji)
    {
        var ownerPeer = new Peer(PeerType.User, senderId);
        var senderPeer = new Peer(PeerType.User, senderId);

        IMessageMedia? media = null;
        var messageType = MessageType.Text;
        var sendType = SendMessageType.Text;
        var actionType = MessageActionType.None;
        List<ReactionCount>? reactions = null;

        switch (kind)
        {
            case MessageKind.Media:
                media = new TMessageMediaPhoto();
                messageType = mediaType;
                sendType = SendMessageType.Media;
                break;
            case MessageKind.Reaction:
                reactions = new List<ReactionCount>
                {
                    new(new TReactionEmoji { Emoticon = emoji }, 1, emoji, null)
                };
                break;
            case MessageKind.Call:
                messageType = MessageType.PhoneCall;
                actionType = MessageActionType.PhoneCall;
                sendType = SendMessageType.MessageService;
                break;
            case MessageKind.Text:
            default:
                text = string.IsNullOrEmpty(text) ? "hi" : text;
                break;
        }

        var item = new MessageItem(
            OwnerPeer: ownerPeer,
            ToPeer: toPeer,
            SenderPeer: senderPeer,
            SenderUserId: senderId,
            MessageId: msgId,
            Message: text,
            Date: 1_700_000_000,
            RandomId: randomId,
            IsOut: false,
            SendMessageType: sendType,
            MessageType: messageType,
            MessageActionType: actionType,
            Media: media,
            Reactions: reactions);

        return new MessageCase(item, kind, toPeer.PeerType);
    }

    private static string ValidP256dh()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdh.ExportParameters(false);
        var x = p.Q.X!;
        var y = p.Q.Y!;
        var pub = new byte[65];
        pub[0] = 0x04;
        x.CopyTo(pub, 1);
        y.CopyTo(pub, 33);
        return Base64UrlReference.Encode(pub);
    }

    private static string ValidAuth() => Base64UrlReference.Encode(RandomNumberGenerator.GetBytes(16));

    private static string BuildWebPushJson(bool includeEndpoint, string? p256dh, string? auth)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            if (includeEndpoint)
            {
                w.WriteString("endpoint", "https://push.example.com/" + Guid.NewGuid().ToString("N"));
            }
            w.WritePropertyName("keys");
            w.WriteStartObject();
            if (p256dh is not null)
            {
                w.WriteString("p256dh", p256dh);
            }
            if (auth is not null)
            {
                w.WriteString("auth", auth);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static PushConfig BuildPushConfig(bool master, bool fcm, bool apns, bool web)
    {
        var cfg = new PushConfig { Enabled = master };
        if (fcm)
        {
            cfg.Fcm.ServiceAccountJson = "{\"type\":\"service_account\"}";
        }
        if (apns)
        {
            cfg.Apns.AuthKeyP8 = "-----BEGIN PRIVATE KEY-----\nMOCK\n-----END PRIVATE KEY-----";
            cfg.Apns.KeyId = "ABC123DEFG";
            cfg.Apns.TeamId = "TEAM123456";
            cfg.Apns.BundleId = "com.example.app";
        }
        if (web)
        {
            cfg.WebPush.VapidPrivateKey = Base64UrlReference.Encode(RandomNumberGenerator.GetBytes(32));
            cfg.WebPush.VapidPublicKey = Base64UrlReference.Encode(RandomNumberGenerator.GetBytes(65));
            cfg.WebPush.VapidSubject = "mailto:admin@example.com";
        }

        return cfg;
    }
}
