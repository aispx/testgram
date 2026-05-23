using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

public static class StoryHelper
{
public static IStoryItem ConvertToStoryItem(StoryDocument doc, long requestingUserId = 0)
{
    if (doc.Deleted || doc.ExpireDate < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    {
        return new TStoryItemDeleted
        {
            Id = doc.StoryId
        };
    }

    if (doc.IsLive)
    {
        return new TStoryItemSkipped
        {
            Id = doc.StoryId,
            Date = (int)doc.Date,
            ExpireDate = (int)doc.ExpireDate,
            Live = true,
            CloseFriends = doc.CloseFriends
        };
    }

    return ConvertToStoryItemInternal(doc, requestingUserId);
}

    private static IStoryItem ConvertToStoryItemInternal(StoryDocument doc, long requestingUserId = 0)
    {
        if (doc.MediaType == 0 || doc.MediaFileId == 0)
        {
            return new TStoryItemDeleted
            {
                Id = doc.StoryId
            };
        }

        IMessageMedia media;

        if (doc.MediaType == 1 && doc.MediaFileId > 0)
        {
            media = new TMessageMediaPhoto
            {
                Photo = new TPhoto
                {
                    Id = doc.MediaFileId,
                    AccessHash = doc.MediaAccessHash,
                    FileReference = doc.MediaFileReference ?? [],
                    Date = (int)doc.Date,
                    Sizes = new TVector<IPhotoSize>
                    {
                        new TPhotoSize { Type = "x", W = 720, H = 1280, Size = doc.MediaSize > 0 ? (int)doc.MediaSize : 100000 },
                        new TPhotoSize { Type = "m", W = 360, H = 640, Size = doc.MediaSize > 0 ? (int)(doc.MediaSize / 4) : 25000 },
                        new TPhotoSize { Type = "s", W = 180, H = 320, Size = doc.MediaSize > 0 ? (int)(doc.MediaSize / 8) : 6000 }
                    },
                    DcId = doc.MediaDcId > 0 ? doc.MediaDcId : 2
                }
            };
        }
        else if (doc.MediaType == 2 && doc.MediaFileId > 0)
        {
            var attributes = new TVector<IDocumentAttribute>();
            if (doc.VideoWidth.HasValue || doc.VideoHeight.HasValue || doc.VideoDuration.HasValue)
            {
                attributes.Add(new TDocumentAttributeVideo
                {
                    W = doc.VideoWidth ?? 720,
                    H = doc.VideoHeight ?? 1280,
                    Duration = doc.VideoDuration ?? 0,
                    RoundMessage = false,
                    SupportsStreaming = true
                });
            }

            var thumbs = new TVector<IPhotoSize>();
            if (doc.VideoThumbBytes != null && doc.VideoThumbBytes.Length > 0)
            {
                thumbs.Add(new TPhotoStrippedSize { Type = "i", Bytes = doc.VideoThumbBytes });
            }

            media = new TMessageMediaDocument
            {
                Document = new TDocument
                {
                    Id = doc.MediaFileId,
                    AccessHash = doc.MediaAccessHash,
                    FileReference = doc.MediaFileReference ?? [],
                    Date = (int)doc.Date,
                    MimeType = doc.MediaMimeType ?? "video/mp4",
                    Size = doc.MediaSize,
                    DcId = doc.MediaDcId,
                    Attributes = attributes,
                    Thumbs = thumbs
                }
            };
        }
        else
        {
            media = new TMessageMediaEmpty();
        }

        var isOut = doc.OwnerPeerId == requestingUserId;

        IPrivacyRule[]? privacyRules = null;
        if (doc.PrivacyRules != null && doc.PrivacyRules.Count > 0)
        {
            privacyRules = ConvertPrivacyRules(doc.PrivacyRules);
        }

        TVector<IMessageEntity>? entities = null;
        if (!string.IsNullOrEmpty(doc.Entities))
        {
            entities = ParseEntities(doc.Entities);
        }

        IStoryFwdHeader? fwdHeader = null;
        if (doc.FwdFromPeerId > 0 && doc.FwdFromStoryId.HasValue)
        {
            fwdHeader = new TStoryFwdHeader
            {
                From = CreatePeer(0, doc.FwdFromPeerId),
                StoryId = doc.FwdFromStoryId.Value
            };
        }

        IPeer? fromId = null;
        if (doc.OwnerPeerType == 0)
        {
            fromId = new TPeerUser { UserId = doc.OwnerPeerId };
        }
        else if (doc.OwnerPeerType == 1)
        {
            fromId = new TPeerChat { ChatId = doc.OwnerPeerId };
        }
        else if (doc.OwnerPeerType == 2)
        {
            fromId = new TPeerChannel { ChannelId = doc.OwnerPeerId };
        }

        return new TStoryItem
        {
            Id = doc.StoryId,
            Date = (int)doc.Date,
            ExpireDate = (int)doc.ExpireDate,
            Caption = doc.Caption,
            Pinned = doc.Pinned,
            Noforwards = doc.NoForwards,
            Out = isOut,
            Edited = doc.Edited,
            Media = media,
            FromId = fromId,
            FwdFrom = fwdHeader,
            Entities = entities,
            Privacy = privacyRules != null ? new TVector<IPrivacyRule>(privacyRules) : null,
            Views = new TStoryViews
            {
                ViewsCount = doc.ViewsCount,
                ForwardsCount = doc.ForwardsCount,
                ReactionsCount = doc.ReactionsCount
            }
        };
    }

    private static IPrivacyRule[] ConvertPrivacyRules(List<StoryPrivacyRule> rules)
    {
        var result = new List<IPrivacyRule>();
        foreach (var rule in rules)
        {
            switch (rule.Type)
            {
                case 0:
                    result.Add(new TPrivacyValueAllowAll());
                    break;
                case 1:
                    result.Add(new TPrivacyValueAllowContacts());
                    break;
                case 2:
                    result.Add(new TPrivacyValueDisallowAll());
                    break;
                case 3:
                    result.Add(new TPrivacyValueDisallowContacts());
                    break;
                case 4:
                    result.Add(new TPrivacyValueAllowCloseFriends());
                    break;
                case 5:
                    if (rule.UserId.HasValue)
                        result.Add(new TPrivacyValueDisallowUsers { Users = new TVector<long> { rule.UserId.Value } });
                    break;
                case 6:
                    if (rule.UserId.HasValue)
                        result.Add(new TPrivacyValueAllowUsers { Users = new TVector<long> { rule.UserId.Value } });
                    break;
            }
        }
        return result.ToArray();
    }

    private static TVector<IMessageEntity>? ParseEntities(string entitiesJson)
    {
        try
        {
            var entitiesData = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(entitiesJson);
            if (entitiesData == null) return null;

            var result = new TVector<IMessageEntity>();
            foreach (var e in entitiesData)
            {
                if (!e.TryGetValue("constructorId", out var constructorIdElem)) continue;
                var constructorId = constructorIdElem.GetInt64();

                if (!e.TryGetValue("offset", out var offsetElem) || !e.TryGetValue("length", out var lengthElem))
                    continue;

                var offset = offsetElem.GetInt32();
                var length = lengthElem.GetInt32();

                if (constructorId == 0xbb92a445)
                {
                    if (e.TryGetValue("url", out var urlElem))
                    {
                        result.Add(new TMessageEntityTextUrl { Offset = offset, Length = length, Url = urlElem.GetString() });
                    }
                }
                else if (constructorId == 0x3cca7d2)
                {
                    if (e.TryGetValue("userId", out var userIdElem))
                    {
                        result.Add(new TMessageEntityMentionName { Offset = offset, Length = length, UserId = userIdElem.GetInt64() });
                    }
                }
                else if (constructorId == 0x73924e66)
                {
                    var entity = new TMessageEntityPre { Offset = offset, Length = length };
                    if (e.TryGetValue("language", out var langElem))
                    {
                        entity.Language = langElem.GetString();
                    }
                    result.Add(entity);
                }
                else
                {
                    result.Add(new TMessageEntityUnknown { Offset = offset, Length = length });
                }
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    public static IPhoto? BuildAlbumIconPhoto(StoryDocument? doc)
    {
        if (doc == null || doc.MediaType != 1 || doc.MediaFileId == 0)
        {
            return null;
        }

        return new TPhoto
        {
            Id = doc.MediaFileId,
            AccessHash = doc.MediaAccessHash,
            FileReference = doc.MediaFileReference ?? [],
            Date = (int)doc.Date,
            Sizes = new TVector<IPhotoSize>
            {
                new TPhotoSize { Type = "x", W = 720, H = 1280, Size = doc.MediaSize > 0 ? (int)doc.MediaSize : 100000 },
                new TPhotoSize { Type = "m", W = 360, H = 640, Size = doc.MediaSize > 0 ? (int)(doc.MediaSize / 4) : 25000 },
                new TPhotoSize { Type = "s", W = 180, H = 320, Size = doc.MediaSize > 0 ? (int)(doc.MediaSize / 8) : 6000 }
            },
            DcId = doc.MediaDcId > 0 ? doc.MediaDcId : 2
        };
    }

    public static IDocument? BuildAlbumIconVideo(StoryDocument? doc)
    {
        if (doc == null || doc.MediaType != 2 || doc.MediaFileId == 0)
        {
            return null;
        }

        var attributes = new TVector<IDocumentAttribute>();
        if (doc.VideoWidth.HasValue || doc.VideoHeight.HasValue || doc.VideoDuration.HasValue)
        {
            attributes.Add(new TDocumentAttributeVideo
            {
                W = doc.VideoWidth ?? 720,
                H = doc.VideoHeight ?? 1280,
                Duration = doc.VideoDuration ?? 0,
                RoundMessage = false,
                SupportsStreaming = true
            });
        }

        var thumbs = new TVector<IPhotoSize>();
        if (doc.VideoThumbBytes != null && doc.VideoThumbBytes.Length > 0)
        {
            thumbs.Add(new TPhotoStrippedSize { Type = "i", Bytes = doc.VideoThumbBytes });
        }

        return new TDocument
        {
            Id = doc.MediaFileId,
            AccessHash = doc.MediaAccessHash,
            FileReference = doc.MediaFileReference != null ? new ReadOnlyMemory<byte>(doc.MediaFileReference) : ReadOnlyMemory<byte>.Empty,
            Date = (int)doc.Date,
            MimeType = doc.MediaMimeType ?? "video/mp4",
            Size = doc.MediaSize,
            DcId = doc.MediaDcId > 0 ? doc.MediaDcId : 2,
            Attributes = attributes,
            Thumbs = thumbs
        };
    }

    public static IPeer CreatePeer(int peerType, long peerId)
    {
        return peerType switch
        {
            0 => new TPeerUser { UserId = peerId },
            1 => new TPeerChat { ChatId = peerId },
            2 => new TPeerChannel { ChannelId = peerId },
            _ => new TPeerUser { UserId = peerId }
        };
    }

    public static (long peerId, int peerType) ResolvePeer(IInputPeer? peer, long defaultUserId)
    {
        long peerId = defaultUserId;
        int peerType = 0;

        switch (peer)
        {
            case TInputPeerSelf:
                peerId = defaultUserId;
                peerType = 0;
                break;
            case TInputPeerUser userPeer:
                peerId = userPeer.UserId;
                peerType = 0;
                break;
            case TInputPeerChannel channelPeer:
                peerId = channelPeer.ChannelId;
                peerType = 2;
                break;
            case TInputPeerChat chatPeer:
                peerId = chatPeer.ChatId;
                peerType = 1;
                break;
        }

        return (peerId, peerType);
    }

    public static int ToStoryPeerType(PeerType peerType)
    {
        return peerType switch
        {
            PeerType.User or PeerType.Self => 0,
            PeerType.Chat => 1,
            PeerType.Channel => 2,
            _ => -1
        };
    }

    public static async Task<bool> CanViewStoryAsync(
        StoryDocument doc,
        long requestingUserId,
        IMongoCollection<BsonDocument>? contactsCollection)
    {
        if (doc.OwnerPeerId == requestingUserId)
            return true;

        if (doc.PrivacyRules == null || doc.PrivacyRules.Count == 0)
            return true;

        bool? isContact = null;
        async Task<bool> CheckIsContact()
        {
            if (isContact.HasValue)
                return isContact.Value;
            if (contactsCollection == null)
            {
                isContact = false;
                return false;
            }
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("selfUserId", doc.OwnerPeerId),
                Builders<BsonDocument>.Filter.Eq("targetUserId", requestingUserId)
            );
            isContact = await contactsCollection.Find(filter).AnyAsync();
            return isContact.Value;
        }

        foreach (var rule in doc.PrivacyRules)
        {
            switch (rule.Type)
            {
                case 0:
                    return true;
                case 2:
                    return false;
                case 1:
                    if (await CheckIsContact())
                        return true;
                    break;
                case 3:
                    if (await CheckIsContact())
                        return false;
                    return true;
                case 4:
                    return false;
                case 5:
                    if (rule.UserId.HasValue && rule.UserId.Value == requestingUserId)
                        return false;
                    break;
                case 6:
                    if (rule.UserId.HasValue && rule.UserId.Value == requestingUserId)
                        return true;
                    return false;
            }
        }
        return true;
    }

    public static bool CanViewStory(StoryDocument doc, long requestingUserId)
    {
        if (doc.OwnerPeerId == requestingUserId)
            return true;

        if (doc.PrivacyRules == null || doc.PrivacyRules.Count == 0)
            return true;

        foreach (var rule in doc.PrivacyRules)
        {
            switch (rule.Type)
            {
                case 0:
                    return true;
                case 2:
                    return false;
            }
        }

        return true;
    }
}
