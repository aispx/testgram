using Google.Protobuf;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Photo;

namespace MyTelegram.Messenger.Services.Impl;

public class MediaHelper(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ICacheManager<UserCacheItem> cacheManager,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IObjectMapper objectMapper,
    ICommandBus commandBus,
    ILogger<MediaHelper> logger,
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
            TMessageMediaInvoice => MessageType.Voice,
            TMessageMediaPhoto => MessageType.Photo,
            TMessageMediaPoll => MessageType.Poll,
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

    private IMessageMedia CreateMediaDice(TInputMediaDice inputMediaDice)
    {
        int value;
        switch (inputMediaDice.Emoticon)
        {
            case "🎰": // Slot machine: 1-64
                value = Random.Shared.Next(1, 65);
                break;
            case "⚽️":
            case "⚽":
            case "🏀": // Football/Basketball: 1-5
                value = Random.Shared.Next(1, 6);
                break;
            default: // Dice/Dart/Bowling: 1-6
                value = Random.Shared.Next(1, 7);
                break;
        }

        return new TMessageMediaDice
        {
            Emoticon = inputMediaDice.Emoticon,
            Value = value
        };
    }

    private IMessageMedia CreateMediaGeoLive(TInputMediaGeoLive inputMediaGeoLive)
    {
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

        return new TMessageMediaGeoLive
        {
            Heading = inputMediaGeoLive.Heading,
            Period = inputMediaGeoLive.Period ?? 0,
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
                var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
                var existingDoc = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", inputDoc.Id)).FirstOrDefaultAsync();
                if (existingDoc != null)
                {
                    long GetInt64(BsonValue v) => v.BsonType switch { BsonType.Int64 => v.AsInt64, BsonType.Int32 => v.AsInt32, _ => v.AsInt64 };
                    int GetInt32(BsonValue v) => v.BsonType switch { BsonType.Int32 => v.AsInt32, BsonType.Int64 => (int)v.AsInt64, _ => v.AsInt32 };
                    
                    var docId = GetInt64(existingDoc["DocumentId"]);
                    var accessHash = GetInt64(existingDoc["AccessHash"]);
                    
                    byte[] fileRefBytes;
                    var fileRef = existingDoc["FileReference"];
                    if (fileRef.BsonType == BsonType.Binary)
                    {
                        fileRefBytes = fileRef.AsBsonBinaryData.Bytes;
                    }
                    else if (fileRef.BsonType == BsonType.Array)
                    {
                        fileRefBytes = fileRef.AsBsonArray.Select(b => (byte)GetInt32(b)).ToArray();
                    }
                    else
                    {
                        fileRefBytes = [];
                    }
                    
                    var mimeType = existingDoc["MimeType"].AsString;
                    var name = existingDoc.Contains("Name") ? existingDoc["Name"].AsString : "";
                    var size = GetInt64(existingDoc["Size"]);
                    var dcId = GetInt32(existingDoc["DcId"]);
                    
                    var attributes = new TVector<IDocumentAttribute>();
                    var thumbs = new TVector<IPhotoSize>();
                    
                    var isTgs = name.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase) || mimeType == "application/x-tgsticker";
                    var isWebp = name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || mimeType == "image/webp";
                    
                    if (isTgs)
                    {
                        mimeType = "application/x-tgsticker";
                        thumbs.Add(new TPhotoSize { Type = "m", W = 100, H = 100, Size = 0 });
                        attributes.Add(new TDocumentAttributeSticker { Alt = "", Stickerset = new TInputStickerSetEmpty() });
                    }
                    else if (isWebp)
                    {
                        mimeType = "image/webp";
                        attributes.Add(new TDocumentAttributeSticker { Alt = "", Stickerset = new TInputStickerSetEmpty() });
                    }
                    
                    logger.LogInformation("CreateMediaOnFileServerAsync: found doc from MongoDB, name={Name}, mime={Mime}, isTgs={IsTgs}, isWebp={IsWebp}", 
                        name, mimeType, isTgs, isWebp);
                    
                    return new TMessageMediaDocument
                    {
                        Document = new TDocument
                        {
                            Id = docId,
                            AccessHash = accessHash,
                            FileReference = fileRefBytes,
                            MimeType = mimeType,
                            Size = size,
                            DcId = dcId,
                            Attributes = attributes,
                            Thumbs = thumbs,
                            VideoThumbs = []
                        }
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

    private Task<IMessageMedia> CreateMediaStoryAsync(TInputMediaStory inputMediaStory)
    {
        throw new NotImplementedException();
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
            case TInputMediaDocument:
            case TInputMediaDocumentExternal:
            case TInputMediaPhoto:
            case TInputMediaPhotoExternal:
            case TInputMediaUploadedDocument:
            case TInputMediaUploadedPhoto:
                return await CreateMediaOnFileServerAsync(media);
            case TInputMediaPaidMedia:
            case TInputMediaGame:
            case TInputMediaInvoice:
                throw new NotImplementedException();
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
            default:
                return null;
        }
    }
}
