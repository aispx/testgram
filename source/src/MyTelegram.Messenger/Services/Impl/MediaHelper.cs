using Google.Protobuf;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Photo;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Dice;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.Stories;

namespace MyTelegram.Messenger.Services.Impl;

public class MediaHelper(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ICacheManager<UserCacheItem> cacheManager,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IObjectMapper objectMapper,
    ICommandBus commandBus,
    ILogger<MediaHelper> logger,
    IFileReferenceHelper fileReferenceHelper,
    IMongoDatabase mongoDatabase)
    : IMediaHelper, ITransientDependency
{
    public MessageType GeMessageType(IMessageMedia? media)
    {
        if (media == null)
        {
            return MessageType.Text;
        }

        return media switch
        {
            TMessageMediaContact => MessageType.Contacts,
            TMessageMediaDice => MessageType.Game,
            TMessageMediaDocument => MessageType.Document,
            TMessageMediaEmpty => MessageType.Text,
            TMessageMediaGame => MessageType.Game,
            TMessageMediaGeo => MessageType.Geo,
            TMessageMediaGeoLive => MessageType.Geo,
            TMessageMediaInvoice => MessageType.Text,
            TMessageMediaPaidMedia => MessageType.Photo,
            TMessageMediaPhoto => MessageType.Photo,
            TMessageMediaPoll => MessageType.Poll,
            TMessageMediaStory => MessageType.Text,
            TMessageMediaToDo => MessageType.Text,
            TMessageMediaUnsupported => MessageType.Text,
            TMessageMediaVenue => MessageType.Geo,
            TMessageMediaWebPage => MessageType.Url,
            _ => throw new ArgumentOutOfRangeException(nameof(media))
        };
    }

    public async Task<IEncryptedFile> SaveEncryptedFileAsync(long reqMsgId,
            IInputEncryptedFile encryptedFile)
    {
        var client = GrpcClientFactory.CreateMediaServiceClient(options.CurrentValue.FileServerGrpcServiceUrl);
        var r = await client
            .SaveEncryptedFileAsync(new SaveEncryptedFileRequest
            {
                EncryptedFile = ByteString.CopyFrom(encryptedFile.ToBytes()),
                ReqMsgId = reqMsgId
            }).ResponseAsync;

        return new TEncryptedFile
        {
            AccessHash = r.AccessHash,
            DcId = r.DcId,
            Id = r.Id,
            KeyFingerprint = r.KeyFingerprint,
            Size = r.Size
        };
    }

    public Task<IMessageMedia?> SaveMediaAsync(IInputMedia? media)
    {
        return SaveMediaCoreAsync(media);
    }

    private async Task CreatePhotoAsync(long userId, IPhoto photo, bool hasVideo, int size, bool isProfilePhoto)
    {

        switch (photo)
        {
            case TPhoto photo1:
                var command = new CreatePhotoCommand(PhotoId.Create(photo1.Id),
                    userId,
                    new PhotoItem(photo1.Id, photo1.AccessHash, photo1.FileReference, photo1.Date,
                        photo1.DcId,
                        size,
                        false,
                        hasVideo,
                        IsProfilePhoto: isProfilePhoto,
                        Sizes2: [.. photo1.Sizes],
                        VideoSizes2: photo1.VideoSizes?.ToList()
                    )
                );
                await commandBus.PublishAsync(command);
                break;
        }
    }

    private List<PhotoSize>? ToPhotoSize(TVector<IPhotoSize> photoSizes)
    {
        List<PhotoSize>? sizes = null;
        foreach (var photoSize in photoSizes)
        {
            switch (photoSize)
            {
                case TPhotoSize photoSize1:
                    sizes ??= [];
                    sizes.Add(new PhotoSize(photoSize1.W, photoSize1.H, photoSize1.Size, photoSize1.Type));
                    break;
            }
        }

        return sizes;
    }

    private List<VideoSize>? ToVideoSizes(TVector<IVideoSize>? videoSizes)
    {
        List<VideoSize>? sizes = null;
        foreach (var videoSize in videoSizes ?? [])
        {
            switch (videoSize)
            {
                case TVideoSize videoSize1:
                    sizes ??= [];
                    sizes.Add(new VideoSize(videoSize1.W, videoSize1.H, videoSize1.Size, videoSize1.Type, videoSize1.VideoStartTs ?? 0));
                    break;
            }
        }

        return sizes;
    }

    public async Task<SavePhotoResult> SavePhotoAsync(long reqMsgId,
            long userId,
        long fileId,
        bool hasVideo,
        double? videoStartTs,
        int parts,
        string? name,
        string? md5,
        IVideoSize? videoEmojiMarkup = null,
        bool isProfilePhoto = false
        )
    {
        var client = GrpcClientFactory.CreateMediaServiceClient(options.CurrentValue.FileServerGrpcServiceUrl);

        var r = await client.SavePhotoAsync(new SavePhotoRequest
        {
            UserId = userId,
            FileId = fileId,
            HasVideo = hasVideo,
            Md5 = md5 ?? string.Empty,
            Name = name ?? string.Empty,
            Parts = parts,
            ReqMsgId = reqMsgId,
            VideoStartTs = videoStartTs ?? 0,
            VideoEmojiMarkup = videoEmojiMarkup == null ? ByteString.Empty : ByteString.CopyFrom(videoEmojiMarkup.ToBytes()),
            IsProfilePhoto = isProfilePhoto

        }).ResponseAsync;

        var photo = r.Photo.Memory.ToTObject<IPhoto>();
        await CreatePhotoAsync(userId, photo, hasVideo, (int)r.Size, isProfilePhoto);

        return new SavePhotoResult(r.PhotoId, r.Photo.Memory.ToTObject<IPhoto>());
    }

    private async Task<TMessageMediaContact> CreateMediaContactAsync(TInputMediaContact inputMediaContact)
    {
        var cachedUserItem = await cacheManager
                .GetAsync(UserCacheItem.GetCacheKey(inputMediaContact.PhoneNumber))
            ;
        return new TMessageMediaContact
        {
            FirstName = inputMediaContact.FirstName,
            LastName = inputMediaContact.LastName ?? string.Empty,
            PhoneNumber = inputMediaContact.PhoneNumber?.Replace(" ", string.Empty) ?? string.Empty,
            Vcard = inputMediaContact.Vcard ?? string.Empty,
            UserId = cachedUserItem?.UserId ?? 0
        };
    }

    /// <summary>
    /// Rolls a <a href="https://corefork.telegram.org/api/dice">dice</a>. The value is the server's, and
    /// only the server's: <c>inputMediaDice</c> carries nothing but the emoji, and an emoji the server never
    /// advertised in <c>emojies_send_dice</c> is refused — accepting an arbitrary one produces a
    /// <c>messageMediaDice</c> for which no client can resolve a sticker set, an empty bubble that never
    /// resolves and logs nothing.
    /// </summary>
    private static IMessageMedia CreateMediaDice(TInputMediaDice inputMediaDice)
    {
        return new TMessageMediaDice
        {
            Emoticon = inputMediaDice.Emoticon,
            Value = DiceEmojiHelper.Roll(inputMediaDice.Emoticon)
        };
    }

    private IMessageMedia CreateMediaGeoLive(TInputMediaGeoLive inputMediaGeoLive)
    {
        // Reject out-of-range period/heading/proximity before anything is stored, matching the
        // server-side limits TDLib enforces (Location.cpp process_live_location).
        GeoLiveHelper.Validate(inputMediaGeoLive, forEdit: false);

        IGeoPoint geo = new TGeoPointEmpty();
        if (inputMediaGeoLive.GeoPoint is TInputGeoPoint inputGeoPoint1)
        {
            geo = new TGeoPoint
            {
                AccuracyRadius = inputGeoPoint1.AccuracyRadius,
                Lat = inputGeoPoint1.Lat,
                Long = inputGeoPoint1.Long,
                AccessHash = Random.Shared.NextInt64()
            };
        }

        // The receiving messageMediaGeoLive has no "stopped" flag: a client decides the location is
        // over when date + period is in the past. A location that is somehow already stopped when it
        // is first sent therefore gets the smallest period that expires immediately.
        // See https://corefork.telegram.org/api/live-location
        var period = inputMediaGeoLive.Stopped
            ? 1
            : inputMediaGeoLive.Period ?? 0;

        return new TMessageMediaGeoLive
        {
            Heading = GeoLiveHelper.NormalizeHeading(inputMediaGeoLive.Heading),
            Period = period,
            ProximityNotificationRadius = inputMediaGeoLive.ProximityNotificationRadius,
            Geo = geo
        };
    }

    private IMessageMedia CreateMediaGeoPoint(TInputMediaGeoPoint inputMediaGeoPoint)
    {
        switch (inputMediaGeoPoint.GeoPoint)
        {
            case TInputGeoPoint inputGeoPoint1:
                return new TMessageMediaGeo
                {
                    Geo = new TGeoPoint
                    {
                        AccuracyRadius = inputGeoPoint1.AccuracyRadius,
                        Lat = inputGeoPoint1.Lat,
                        Long = inputGeoPoint1.Long,
                        AccessHash = Random.Shared.NextInt64()
                    }
                };
        }

        return new TMessageMediaGeo
        {
            Geo = new TGeoPointEmpty()
        };
    }

    private async Task<IMessageMedia> CreateMediaOnFileServerAsync(IInputMedia media)
    {
        try
        {
            // Check if this is an existing document (from sticker pack)
            logger.LogInformation("CreateMediaOnFileServerAsync: media type = {Type}", media.GetType().Name);
            if (media is TInputMediaDocument inputMediaDocument && inputMediaDocument.Id is TInputDocument inputDoc && inputDoc.Id > 0)
            {
                var existingDoc = await mongoDatabase
                    .GetCollection<DocumentReadModel>("eventflow-documentreadmodel")
                    .Find(Builders<DocumentReadModel>.Filter.Eq(p => p.DocumentId, inputDoc.Id))
                    .FirstOrDefaultAsync();
                if (existingDoc != null)
                {
                    // Map through DocumentMapper. Building the TDocument by hand here used to drop
                    // every attribute except a synthesised sticker one, so re-sending an existing
                    // document lost its real attributes: a GIF arrived without
                    // documentAttributeAnimated and rendered as a plain video, a video lost its
                    // dimensions and duration, and every file lost its name. The mapper also carries
                    // the thumbnails and the date across.
                    var document = objectMapper.Map<IDocumentReadModel, TDocument>(existingDoc);
                    document.Thumbs ??= [];
                    document.VideoThumbs ??= [];
                    document.Attributes ??= [];

                    var name = existingDoc.Name ?? string.Empty;
                    var isTgs = name.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase) ||
                                document.MimeType == "application/x-tgsticker";
                    var isWebp = name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                                 document.MimeType == "image/webp";

                    if (isTgs)
                    {
                        document.MimeType = "application/x-tgsticker";
                        if (document.Thumbs.Count == 0)
                        {
                            document.Thumbs.Add(new TPhotoSize { Type = "m", W = 100, H = 100, Size = 0 });
                        }
                    }
                    else if (isWebp)
                    {
                        document.MimeType = "image/webp";
                    }

                    if ((isTgs || isWebp) && !document.Attributes.OfType<TDocumentAttributeSticker>().Any())
                    {
                        document.Attributes.Add(new TDocumentAttributeSticker
                        {
                            Alt = "",
                            Stickerset = new TInputStickerSetEmpty()
                        });
                    }

                    logger.LogInformation("CreateMediaOnFileServerAsync: found doc from MongoDB, name={Name}, mime={Mime}, isTgs={IsTgs}, isWebp={IsWebp}, attributes={Attributes}",
                        name, document.MimeType, isTgs, isWebp, document.Attributes.Count);

                    return new TMessageMediaDocument
                    {
                        Document = document
                    };
                }
            }

            var client = GrpcClientFactory.CreateMediaServiceClient(options.CurrentValue.FileServerGrpcServiceUrl);
            var r = await client.SaveMediaAsync(new SaveMediaRequest
            {
                Media = ByteString.CopyFrom(media.ToBytes())
            })
                .ResponseAsync;
            var result = r.Media.Memory.ToTObject<IMessageMedia>();

            // If .tgs, .webp or has sticker attribute - add sticker attributes
            if (result is TMessageMediaDocument { Document: TDocument doc })
            {
                var fileName = media is TInputMediaUploadedDocument uploaded ? 
                    string.Join(",", uploaded.Attributes.OfType<TDocumentAttributeFilename>().Select(a => a.FileName)) : "";
                var hasStickerAttr = doc.Attributes.OfType<TDocumentAttributeSticker>().Any();
                var hasUploadStickerAttr = media is TInputMediaUploadedDocument u && u.Attributes.OfType<TDocumentAttributeSticker>().Any();
                
                var isSticker = doc.MimeType == "application/x-tgsticker" ||
                                doc.MimeType == "image/webp" ||
                                hasStickerAttr ||
                                hasUploadStickerAttr ||
                                (media is TInputMediaUploadedDocument up && 
                                    (up.Attributes.OfType<TDocumentAttributeFilename>().Any(a => 
                                                                                
                                        a.FileName.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase) || 
                                        a.FileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))));
                
                logger.LogInformation("SaveMedia: original mime={Mime}, fileName='{FileName}', hasStickerAttr={HasSticker}, hasUploadStickerAttr={HasUploadSticker}, isSticker={IsSticker}", 
                    doc.MimeType, fileName, hasStickerAttr, hasUploadStickerAttr, isSticker);
                
                if (isSticker)
                {
                    if (fileName.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase))
                    {
                        doc.MimeType = "application/x-tgsticker";
                        if (doc.Thumbs == null || doc.Thumbs.Count == 0)
                        {
                            doc.Thumbs = [new TPhotoSize { Type = "m", W = 100, H = 100, Size = 0 }];
                            logger.LogDebug("Added placeholder thumbnail for tgs");
                        }
                    }
                    else if (fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    {
                        doc.MimeType = "image/webp";
                    }
                    if (!hasStickerAttr)
                    {
                        doc.Attributes = [.. doc.Attributes, new TDocumentAttributeSticker
                        {
                            Alt = "",
                            Stickerset = new TInputStickerSetEmpty()
                        }];
                        logger.LogDebug("Added sticker attribute");
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Save media failed");
            RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
        }

        throw new InvalidOperationException();
    }

    private IMessageMedia CreateMediaPoll(TInputMediaPoll inputMediaPoll)
    {
        return new TMessageMediaPoll
        {
            Poll = inputMediaPoll.Poll,
            Results = new TPollResults()
        };
    }

    /// <summary>
    /// Resolves a story reference into <c>messageMediaStory</c>, so a story can be forwarded into a chat.
    /// <para>
    /// The story itself is inlined when it is still available; otherwise only the peer and id go out and
    /// the client renders an "expired story" placeholder.
    /// </para>
    /// </summary>
    private async Task<IMessageMedia> CreateMediaStoryAsync(TInputMediaStory inputMediaStory)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(inputMediaStory.Peer, 0);

        var storyCollection = mongoDatabase.GetCollection<StoryDocument>("stories");
        var story = await storyCollection
            .Find(s => s.OwnerPeerId == ownerPeerId &&
                       s.OwnerPeerType == ownerPeerType &&
                       s.StoryId == inputMediaStory.Id &&
                       !s.Deleted)
            .FirstOrDefaultAsync();

        return new TMessageMediaStory
        {
            Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
            Id = inputMediaStory.Id,
            Story = story != null ? StoryHelper.ConvertToStoryItem(fileReferenceHelper, story) : null
        };
    }

    private IMessageMedia CreateMediaVenue(TInputMediaVenue inputMediaVenue)
    {
        TInputGeoPoint? inputGeoPoint = null;
        if (inputMediaVenue.GeoPoint is TInputGeoPoint geoPoint)
        {
            inputGeoPoint = geoPoint;
        }

        return new TMessageMediaVenue
        {
            Title = inputMediaVenue.Title,
            Address = inputMediaVenue.Address,
            Provider = inputMediaVenue.Provider,
            VenueId = inputMediaVenue.VenueId,
            VenueType = inputMediaVenue.VenueType,
            Geo = inputGeoPoint == null
                ? new TGeoPointEmpty()
                : new TGeoPoint
                {
                    AccuracyRadius = inputGeoPoint.AccuracyRadius,
                    Lat = inputGeoPoint.Lat,
                    Long = inputGeoPoint.Long
                }
        };
    }

    private Task<IMessageMedia> CreateMediaWebPageAsync(TInputMediaWebPage inputMediaWebPage)
    {
        throw new NotImplementedException();
    }

    private async Task<IMessageMedia?> SaveMediaCoreAsync(IInputMedia? media)
    {
        switch (media)
        {
            case TInputMediaContact inputMediaContact:
                return await CreateMediaContactAsync(inputMediaContact);
            case TInputMediaDice inputMediaDice:
                return CreateMediaDice(inputMediaDice);
            case TInputMediaStakeDice:
                // The TON stake dice half of https://corefork.telegram.org/api/dice is not served here:
                // there is no wallet and no commit-reveal, and messages.getEmojiGameInfo answers
                // emojiGameUnavailable. Falling through to the default arm would answer sendMedia
                // successfully with no media at all; Android reacts to an error by re-reading the game info
                // and retrying with a fresh game_hash (SendMessagesHelper.sendMessage), so this has to be
                // an error rather than a silent success.
                RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();

                return null;
            case TInputMediaDocument:
            case TInputMediaDocumentExternal:
            case TInputMediaPhoto:
            case TInputMediaPhotoExternal:
            case TInputMediaUploadedDocument:
            case TInputMediaUploadedPhoto:
                return await CreateMediaOnFileServerAsync(media);
            case TInputMediaPaidMedia inputMediaPaidMedia:
                return CreateMediaPaidMedia(inputMediaPaidMedia);
            case TInputMediaGame:
                throw new NotImplementedException();
            case TInputMediaInvoice inputMediaInvoice:
                return CreateMediaInvoice(inputMediaInvoice);
            case TInputMediaEmpty:
                return new TMessageMediaEmpty();
            case TInputMediaGeoLive inputMediaGeoLive:
                return CreateMediaGeoLive(inputMediaGeoLive);
            case TInputMediaGeoPoint inputMediaGeoPoint:
                return CreateMediaGeoPoint(inputMediaGeoPoint);
            case TInputMediaPoll inputMediaPoll:
                return CreateMediaPoll(inputMediaPoll);
            case TInputMediaStory inputMediaStory:
                return await CreateMediaStoryAsync(inputMediaStory);
            case TInputMediaVenue inputMediaVenue:
                return CreateMediaVenue(inputMediaVenue);
            case TInputMediaWebPage inputMediaWebPage:
                return await CreateMediaWebPageAsync(inputMediaWebPage);
            case TInputMediaTodo inputMediaTodo:
                return CreateMediaTodo(inputMediaTodo);
            default:
                return null;
        }
    }

    private static IMessageMedia CreateMediaPaidMedia(TInputMediaPaidMedia inputMediaPaidMedia)
    {
        if (inputMediaPaidMedia.StarsAmount <= 0)
        {
            RpcErrors.RpcErrors400.ExtendedMediaAmountInvalid.ThrowRpcError();
        }

        var extendedMedia = inputMediaPaidMedia.ExtendedMedia;
        if (extendedMedia == null || extendedMedia.Count == 0)
        {
            RpcErrors.RpcErrors400.ExtendedMediaInvalid.ThrowRpcError();
            return null!;
        }

        if (extendedMedia.Any(p => !IsAllowedPaidMediaItem(p)))
        {
            RpcErrors.RpcErrors400.ExtendedMediaInvalid.ThrowRpcError();
        }

        return new TMessageMediaPaidMedia
        {
            StarsAmount = inputMediaPaidMedia.StarsAmount,
            ExtendedMedia = new TVector<IMessageExtendedMedia>(
                extendedMedia.Select(_ => (IMessageExtendedMedia)new TMessageExtendedMediaPreview()))
        };
    }

    private static bool IsAllowedPaidMediaItem(IInputMedia media)
    {
        return media is TInputMediaPhoto
            or TInputMediaUploadedPhoto
            or TInputMediaPhotoExternal
            or TInputMediaDocument
            or TInputMediaUploadedDocument
            or TInputMediaDocumentExternal;
    }

    /// <summary>
    /// Builds the media for a freshly sent checklist, which by definition has no completions yet.
    /// Editing an existing checklist must NOT go through here — it has to carry the previous
    /// completions over (see EditMessageHandler), otherwise every edit would clear all the ticks.
    /// </summary>
    private IMessageMedia CreateMediaTodo(TInputMediaTodo inputMediaTodo)
    {
        return TodoMediaFactory.Create(inputMediaTodo.Todo, []);
    }

    /// <summary>
    /// Builds the client facing half of an invoice.
    /// </summary>
    /// <remarks>
    /// Everything the payment flow runs on — payload, provider token, and the <c>invoice</c> flags
    /// beyond <c>shipping_address_requested</c> — has no place in <c>messageMediaInvoice</c> and is
    /// kept server side by <see cref="BotInvoiceHelper"/> instead, which <c>SendMediaHandler</c> writes
    /// alongside the message.
    /// </remarks>
    private IMessageMedia CreateMediaInvoice(TInputMediaInvoice inputMediaInvoice)
    {
        var invoice = inputMediaInvoice.Invoice;

        return new TMessageMediaInvoice
        {
            Title = inputMediaInvoice.Title,
            Description = inputMediaInvoice.Description,
            // inputWebDocument does not implement IWebDocument, so the old `as IWebDocument` cast
            // silently dropped every bot supplied invoice photo.
            Photo = BotInvoiceHelper.ToWebDocument(inputMediaInvoice.Photo),
            Currency = invoice?.Currency ?? BotInvoiceHelper.StarsCurrency,
            TotalAmount = BotInvoiceHelper.GetTotalAmount(invoice),
            StartParam = inputMediaInvoice.StartParam ?? string.Empty,
            ShippingAddressRequested = invoice?.ShippingAddressRequested ?? false,
            Test = invoice?.Test ?? false
        };
    }
}
