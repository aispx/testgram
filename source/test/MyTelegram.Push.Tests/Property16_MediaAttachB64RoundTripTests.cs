// Feature: push-updates, Property 16: Медиа round-trip через attachb64 (media round-trip via attachb64)
//
// For any media message (Photo / Document), MessagePushDataBuilder fills custom.attachb64 with the
// base64url TL-serialization of the corresponding Photo / Document object, and decoding that string
// (base64url -> TL bytes -> deserialize) restores the original object (Requirement 4.8).
//
// This drives the REAL builder over generated media messages, then verifies the production wire path
// end-to-end: the attachb64 string is decoded with an independent base64url reference codec and the
// bytes are deserialized back into a Photo / Document via the same TL serializer production uses
// (ToTObject). Round-trip is asserted by re-serializing the decoded object and comparing the TL bytes
// to the original (a byte-exact equality that proves the decoded object equals the original).
//
// Validates: Requirements 4.8

using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property16_MediaAttachB64RoundTripTests
{
    // BuildForPersonalMessageAsync only consults IUserAppService to resolve a display name and falls
    // back to "Unknown" on any failure, so a null dependency is sufficient for exercising the builder.
    private static readonly MessagePushDataBuilder Builder = new(userAppService: null!);

    /// <summary>A short opaque byte blob (e.g. a file reference) of arbitrary length.</summary>
    private static Gen<byte[]> Blob =>
        from length in Gen.Choose(0, 24)
        from bytes in GenHelpers.ArrayOfLength(length, Gen.Choose(0, 255).Select(i => (byte)i))
        select bytes;

    /// <summary>Generates a fully-populated TL <see cref="TPhoto"/> with random scalar fields.</summary>
    private static Gen<IObject> Photo =>
        from id in PushGen.PositiveId
        from accessHash in PushGen.PositiveId
        from fileRef in Blob
        from date in Gen.Choose(1, int.MaxValue)
        from dcId in Gen.Choose(1, 5)
        from hasStickers in Arb.Generate<bool>()
        select (IObject)new TPhoto
        {
            HasStickers = hasStickers,
            Id = id,
            AccessHash = accessHash,
            FileReference = fileRef,
            Date = date,
            Sizes = new TVector<IPhotoSize>(),
            DcId = dcId
        };

    /// <summary>Generates a fully-populated TL <see cref="TDocument"/> with random scalar fields.</summary>
    private static Gen<IObject> Document =>
        from id in PushGen.PositiveId
        from accessHash in PushGen.PositiveId
        from fileRef in Blob
        from date in Gen.Choose(1, int.MaxValue)
        from size in PushGen.PositiveId
        from dcId in Gen.Choose(1, 5)
        from mime in Gen.Elements("image/jpeg", "video/mp4", "audio/ogg", "application/pdf")
        select (IObject)new TDocument
        {
            Id = id,
            AccessHash = accessHash,
            FileReference = fileRef,
            Date = date,
            MimeType = mime,
            Size = size,
            DcId = dcId,
            Attributes = new TVector<IDocumentAttribute>()
        };

    /// <summary>
    /// A media message fixture: a peer (reused task-1 generators), a Photo- or Document-bearing media,
    /// and the original TL object the attachb64 must round-trip back to.
    /// </summary>
    private static Gen<MediaMessageCase> MediaMessage =>
        from peer in PushGen.AnyPeer
        from senderId in PushGen.PooledUserId
        from msgId in Gen.Choose(1, 100000)
        from randomId in PushGen.PositiveId
        from usePhoto in Arb.Generate<bool>()
        from original in (usePhoto ? Photo : Document)
        let media = usePhoto
            ? (IMessageMedia)new TMessageMediaPhoto { Photo = (IPhoto)original }
            : new TMessageMediaDocument { Document = (IDocument)original }
        let messageType = usePhoto ? MessageType.Photo : MessageType.Document
        let item = new MessageItem(
            OwnerPeer: new Peer(PeerType.User, senderId),
            ToPeer: peer,
            SenderPeer: new Peer(PeerType.User, senderId),
            SenderUserId: senderId,
            MessageId: msgId,
            Message: string.Empty,
            Date: 1_700_000_000,
            RandomId: randomId,
            IsOut: false,
            SendMessageType: SendMessageType.Media,
            MessageType: messageType,
            Media: media)
        select new MediaMessageCase(item, original, peer.PeerType);

    // Property 16: Медиа round-trip через attachb64
    // Validates: Requirements 4.8
    [Property(MaxTest = 100)]
    public Property Media_attachb64_is_base64url_tl_serialization_that_round_trips()
    {
        return Prop.ForAll(Arb.From(MediaMessage), mm =>
        {
            var push = mm.PeerType == PeerType.Channel
                ? Builder.BuildForChannelMessageAsync(mm.Item, chatName: "Group").GetAwaiter().GetResult()
                : Builder.BuildForPersonalMessageAsync(mm.Item).GetAwaiter().GetResult();

            push.ShouldNotBeNull();
            push!.Custom.ShouldNotBeNull();

            // custom.attachb64 must be populated for a media message (Requirement 4.8).
            var attachb64 = push.Custom!.Attachb64;
            attachb64.ShouldNotBeNullOrWhiteSpace();

            // It must be valid base64url whose decoding yields the TL bytes of the original object,
            // and deserializing those bytes restores the original Photo / Document.
            var decodedBytes = Base64UrlReference.Decode(attachb64!);
            decodedBytes.ShouldBe(mm.Original.ToBytes());

            var restored = ((ReadOnlyMemory<byte>)decodedBytes).ToTObject<IObject>();
            restored.ShouldNotBeNull();
            restored.ToBytes().ShouldBe(mm.Original.ToBytes());

            return true;
        });
    }

    /// <summary>A generated media message paired with the original TL object its attachb64 encodes.</summary>
    public sealed record MediaMessageCase(MessageItem Item, IObject Original, PeerType PeerType);
}
