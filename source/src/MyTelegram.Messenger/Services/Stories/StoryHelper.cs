using System.Text.Json;
using System.Text.RegularExpressions;
using MyTelegram.Schema;
// EventFlow also ships a JsonSerializer; disambiguate to the BCL one.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Pure conversion and privacy-evaluation helpers for
/// <a href="https://corefork.telegram.org/api/stories">stories</a>. Anything needing I/O lives in
/// <see cref="IStoryAccessService"/> instead, so this class stays directly unit-testable.
/// </summary>
public static partial class StoryHelper
{
    /// <summary>Story-document owner peer type values (see <see cref="ToStoryPeerType"/>).</summary>
    public const int PeerTypeUser = 0;
    public const int PeerTypeChat = 1;
    public const int PeerTypeChannel = 2;

    /// <summary>Story-document <c>MediaType</c> values.</summary>
    public const int MediaTypePhoto = 1;
    public const int MediaTypeVideo = 2;

    [GeneratedRegex(@"#[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex HashtagRegex();

    /// <summary>
    /// Converts a stored story to its TL form.
    /// </summary>
    /// <param name="doc">The stored story.</param>
    /// <param name="requestingUserId">Who is reading; drives the <c>out</c> and <c>min</c> flags.</param>
    /// <param name="sentReaction">
    /// The requesting user's own reaction, when known. Loaded in batch by the callers rather than
    /// per-story, so it is passed in instead of being fetched here.
    /// </param>
    /// <param name="includePrivacy">
    /// Whether to include the story's privacy rules. Only the owner may see them — they are the
    /// owner's private configuration, not viewer-facing data.
    /// </param>
    /// <param name="photo">
    /// The photo read model for this story's media, when the caller loaded it. Supplies the real
    /// per-size breakdown instead of a guess.
    /// </param>
    /// <param name="document">
    /// The document read model for a video story's media, when the caller loaded it. Supplies the
    /// thumbnail the client draws as the preview tile.
    /// </param>
    /// <param name="fileReferenceHelper">
    /// Mints the <c>file_reference</c> of the story's media. A story document stores the reference the
    /// media carried when the story was posted, which is a value that expires, so it is re-derived on
    /// every read. See https://corefork.telegram.org/api/file-references
    /// </param>
    /// <remarks>
    /// Only a genuinely deleted story becomes <c>storyItemDeleted</c>. Expiry alone does not:
    /// per <a href="https://corefork.telegram.org/api/stories">the API</a> an expired story moves to
    /// the archive, and pinning puts it back on the profile — which is precisely what
    /// <c>stories.getPinnedStories</c> and <c>stories.getStoriesArchive</c> serve. Collapsing
    /// expired stories to <c>storyItemDeleted</c> here emptied both of those listings.
    /// </remarks>
    public static IStoryItem ConvertToStoryItem(
        IFileReferenceHelper fileReferenceHelper,
        StoryDocument doc,
        long requestingUserId = 0,
        IReaction? sentReaction = null,
        bool includePrivacy = false,
        IPhotoReadModel? photo = null,
        IDocumentReadModel? document = null)
    {
        if (doc.Deleted)
        {
            return new TStoryItemDeleted
            {
                Id = doc.StoryId
            };
        }

        if (doc.IsLive)
        {
            return ConvertToLiveStoryItem(doc, requestingUserId, sentReaction, includePrivacy);
        }

        return ConvertToStoryItemInternal(fileReferenceHelper, doc, requestingUserId, sentReaction, includePrivacy,
            photo, document);
    }

    private static IStoryItem ConvertToLiveStoryItem(
        StoryDocument doc,
        long requestingUserId,
        IReaction? sentReaction,
        bool includePrivacy)
    {
        if (doc.GroupCallId == 0 || doc.GroupCallAccessHash == 0)
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

        var isOwner = IsOwner(doc, requestingUserId);

        var item = new TStoryItem
        {
            Id = doc.StoryId,
            Date = (int)doc.Date,
            ExpireDate = (int)doc.ExpireDate,
            Caption = doc.Caption,
            Pinned = doc.Pinned,
            Noforwards = doc.NoForwards,
            Out = isOwner,
            Edited = doc.Edited,
            Min = !isOwner,
            Media = new TMessageMediaVideoStream
            {
                RtmpStream = doc.RtmpStream,
                Call = new TInputGroupCall
                {
                    Id = doc.GroupCallId,
                    AccessHash = doc.GroupCallAccessHash
                }
            },
            FromId = CreatePeerOrNull(doc.OwnerPeerType, doc.OwnerPeerId),
            SentReaction = sentReaction,
            MediaAreas = StoryMediaAreaHelper.ToMediaAreas(doc.MediaAreas),
            Albums = doc.AlbumIds is { Count: > 0 } ? new TVector<int>(doc.AlbumIds) : null,
            Views = BuildViews(doc)
        };

        ApplyPrivacy(item, doc, includePrivacy);

        return item;
    }

    private static IStoryItem ConvertToStoryItemInternal(
        IFileReferenceHelper fileReferenceHelper,
        StoryDocument doc,
        long requestingUserId,
        IReaction? sentReaction,
        bool includePrivacy,
        IPhotoReadModel? photo = null,
        IDocumentReadModel? document = null)
    {
        if (doc.MediaType == 0 || doc.MediaFileId == 0)
        {
            return new TStoryItemDeleted
            {
                Id = doc.StoryId
            };
        }

        // Some stories were seeded with 1x1-pixel placeholder objects standing in for real media.
        // Advertising their sizes makes the client download 284 bytes of single-pixel JPEG and
        // stretch it across the tile — a flat block of colour. There is nothing to show, so say so
        // rather than render garbage. MediaUnusable is set by the media-verification pass.
        if (doc.MediaUnusable)
        {
            return new TStoryItemDeleted
            {
                Id = doc.StoryId
            };
        }

        IMessageMedia media = doc.MediaType switch
        {
            1 => new TMessageMediaPhoto { Photo = BuildPhoto(fileReferenceHelper, doc, photo) },
            2 => new TMessageMediaDocument { Document = BuildDocument(fileReferenceHelper, doc, document) },
            _ => new TMessageMediaEmpty()
        };

        var isOwner = IsOwner(doc, requestingUserId);

        var item = new TStoryItem
        {
            Id = doc.StoryId,
            Date = (int)doc.Date,
            ExpireDate = (int)doc.ExpireDate,
            Caption = doc.Caption,
            Pinned = doc.Pinned,
            Noforwards = doc.NoForwards,
            Out = isOwner,
            Edited = doc.Edited,
            Min = !isOwner,
            Media = media,
            FromId = CreatePeerOrNull(doc.OwnerPeerType, doc.OwnerPeerId),
            FwdFrom = BuildFwdHeader(doc),
            Entities = ParseEntities(doc.Entities),
            MediaAreas = StoryMediaAreaHelper.ToMediaAreas(doc.MediaAreas),
            Albums = doc.AlbumIds is { Count: > 0 } ? new TVector<int>(doc.AlbumIds) : null,
            Music = BuildMusic(fileReferenceHelper, doc),
            SentReaction = sentReaction,
            Views = BuildViews(doc)
        };

        ApplyPrivacy(item, doc, includePrivacy);

        return item;
    }

    private static bool IsOwner(StoryDocument doc, long requestingUserId)
    {
        return doc.OwnerPeerType == PeerTypeUser && doc.OwnerPeerId == requestingUserId;
    }

    private static TStoryViews BuildViews(StoryDocument doc)
    {
        return new TStoryViews
        {
            ViewsCount = doc.ViewsCount,
            ForwardsCount = doc.ForwardsCount > 0 ? doc.ForwardsCount : null,
            ReactionsCount = doc.ReactionsCount > 0 ? doc.ReactionsCount : null
        };
    }

    /// <summary>
    /// Sets the audience flags every viewer needs (so clients can render "close friends only" etc.)
    /// and, for the owner only, the full privacy rule list.
    /// </summary>
    private static void ApplyPrivacy(TStoryItem item, StoryDocument doc, bool includePrivacy)
    {
        var rules = doc.PrivacyRules;

        if (rules == null || rules.Count == 0)
        {
            // No rules stored means the story was never restricted.
            item.Public = true;
            item.CloseFriends = doc.CloseFriends;
            return;
        }

        foreach (var rule in rules)
        {
            switch (rule.Type)
            {
                case StoryPrivacyRuleType.AllowAll:
                    item.Public = true;
                    break;
                case StoryPrivacyRuleType.AllowContacts:
                    item.Contacts = true;
                    break;
                case StoryPrivacyRuleType.AllowCloseFriends:
                    item.CloseFriends = true;
                    break;
                case StoryPrivacyRuleType.AllowUsers:
                    item.SelectedContacts = true;
                    break;
            }
        }

        // Live stories carry close_friends on the document itself.
        if (doc.CloseFriends)
        {
            item.CloseFriends = true;
        }

        if (includePrivacy)
        {
            var converted = ConvertPrivacyRules(rules);
            if (converted.Count > 0)
            {
                item.Privacy = converted;
            }
        }
    }

    private static IStoryFwdHeader? BuildFwdHeader(StoryDocument doc)
    {
        if (doc.FwdFromPeerId == 0 || !doc.FwdFromStoryId.HasValue)
        {
            return null;
        }

        return new TStoryFwdHeader
        {
            From = CreatePeer(doc.FwdFromPeerType, doc.FwdFromPeerId),
            StoryId = doc.FwdFromStoryId.Value,
            Modified = doc.FwdModified
        };
    }

    private static IDocument? BuildMusic(IFileReferenceHelper fileReferenceHelper, StoryDocument doc)
    {
        if (!doc.MusicDocumentId.HasValue || doc.MusicDocumentId.Value == 0)
        {
            return null;
        }

        return new TDocument
        {
            Id = doc.MusicDocumentId.Value,
            AccessHash = doc.MusicAccessHash ?? 0,
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, doc.MusicDocumentId.Value),
            Date = (int)doc.Date,
            MimeType = "audio/mpeg",
            Size = 0,
            DcId = doc.MediaDcId > 0 ? doc.MediaDcId : 2,
            Attributes = new TVector<IDocumentAttribute>(),
            Thumbs = new TVector<IPhotoSize>()
        };
    }

    /// <summary>Builds the photo for a photo story or an album cover.</summary>
    /// <param name="doc">The stored story.</param>
    /// <param name="photo">
    /// The photo read model for <c>doc.MediaFileId</c>, when the caller loaded it. It carries the
    /// real per-size breakdown; a story document only stores the media id, hash and file reference.
    /// </param>
    public static IPhoto BuildPhoto(IFileReferenceHelper fileReferenceHelper, StoryDocument doc,
        IPhotoReadModel? photo = null)
    {
        return new TPhoto
        {
            Id = doc.MediaFileId,
            AccessHash = doc.MediaAccessHash,
            FileReference = fileReferenceHelper.Create(AccessHashType.Photo, doc.MediaFileId),
            Date = (int)doc.Date,
            Sizes = BuildPhotoSizes(doc, photo),
            DcId = doc.MediaDcId > 0 ? doc.MediaDcId : 2
        };
    }

    /// <summary>Builds the document for a video story or an album cover.</summary>
    /// <param name="doc">The stored story.</param>
    /// <param name="document">
    /// The document read model for <c>doc.MediaFileId</c>, when the caller loaded it. It carries the
    /// thumbnail sizes; a story document only has an inline thumbnail if one was captured at upload
    /// time, and an empty <c>thumbs</c> leaves the client with nothing to draw as the preview tile.
    /// </param>
    public static IDocument BuildDocument(IFileReferenceHelper fileReferenceHelper, StoryDocument doc,
        IDocumentReadModel? document = null)
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

        // An inline stripped thumbnail renders instantly, so prefer it when the upload captured one.
        if (doc.StrippedThumbBytes is { Length: > 0 })
        {
            thumbs.Add(new TPhotoStrippedSize { Type = "i", Bytes = doc.StrippedThumbBytes });
        }

        // Then the downloadable thumbnail sizes recorded for the document itself.
        foreach (var size in document?.Thumbs ?? [])
        {
            thumbs.Add(size.Type == "i"
                ? new TPhotoStrippedSize { Type = size.Type, Bytes = size.StrippedThumb }
                : new TPhotoSize { Type = size.Type, W = size.W, H = size.H, Size = (int)size.Size });
        }

        return new TDocument
        {
            Id = doc.MediaFileId,
            AccessHash = doc.MediaAccessHash,
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, doc.MediaFileId),
            Date = (int)doc.Date,
            MimeType = doc.MediaMimeType ?? "video/mp4",
            Size = doc.MediaSize,
            DcId = doc.MediaDcId > 0 ? doc.MediaDcId : 2,
            Attributes = attributes,
            Thumbs = thumbs
        };
    }

    private static TVector<IPhotoSize> BuildPhotoSizes(StoryDocument doc, IPhotoReadModel? photo)
    {
        var sizes = new TVector<IPhotoSize>();

        // The inline preview first: the client renders the profile tile from this without a round
        // trip, and treats a thumbnail list without one as having no placeholder at all.
        if (doc.StrippedThumbBytes is { Length: > 0 })
        {
            sizes.Add(new TPhotoStrippedSize { Type = "i", Bytes = doc.StrippedThumbBytes });
        }

        // Then the real breakdown. Guessed sizes make the client ask for byte ranges the file does
        // not have, and the image silently never renders.
        var real = ToPhotoSizes(photo);
        if (real.Count > 0)
        {
            foreach (var size in real)
            {
                sizes.Add(size);
            }

            return sizes;
        }

        // No photo read model, and no stored size either — advertising a made-up length here is
        // worse than advertising nothing: the client fetches it, the file-server 404s because the
        // object does not exist, and the client retries the same missing size. Offer the single
        // base object instead and let the client size it from the bytes it receives.
        if (doc.MediaSize <= 0)
        {
            sizes.Add(new TPhotoSize { Type = "x", W = 720, H = 1280, Size = 0 });
            return sizes;
        }

        // Fall back to a proportional guess from the stored media size.
        sizes.Add(new TPhotoSize { Type = "x", W = 720, H = 1280, Size = (int)doc.MediaSize });
        sizes.Add(new TPhotoSize { Type = "m", W = 360, H = 640, Size = (int)(doc.MediaSize / 4) });
        sizes.Add(new TPhotoSize { Type = "s", W = 180, H = 320, Size = (int)(doc.MediaSize / 8) });
        return sizes;
    }

    /// <summary>
    /// Maps a photo read model's stored sizes onto their TL forms, preferring the newer
    /// <c>Sizes2</c> shape and falling back to the legacy <c>Sizes</c> list.
    /// </summary>
    private static TVector<IPhotoSize> ToPhotoSizes(IPhotoReadModel? photo)
    {
        var result = new TVector<IPhotoSize>();
        if (photo == null)
        {
            return result;
        }

        if (photo.Sizes2 is { Count: > 0 })
        {
            foreach (var size in photo.Sizes2)
            {
                result.Add(size);
            }

            return result;
        }

        foreach (var size in photo.Sizes ?? [])
        {
            // A stripped thumbnail carries inline bytes rather than a downloadable size.
            result.Add(size.Type == "i"
                ? new TPhotoStrippedSize { Type = size.Type, Bytes = size.StrippedThumb }
                : new TPhotoSize { Type = size.Type, W = size.W, H = size.H, Size = (int)size.Size });
        }

        return result;
    }

    /// <summary>Album cover photo, or null when the story is not a photo story.</summary>
    public static IPhoto? BuildAlbumIconPhoto(IFileReferenceHelper fileReferenceHelper, StoryDocument? doc)
    {
        if (doc == null || doc.MediaType != 1 || doc.MediaFileId == 0)
        {
            return null;
        }

        return BuildPhoto(fileReferenceHelper, doc);
    }

    /// <summary>Album cover video, or null when the story is not a video story.</summary>
    public static IDocument? BuildAlbumIconVideo(IFileReferenceHelper fileReferenceHelper, StoryDocument? doc)
    {
        if (doc == null || doc.MediaType != 2 || doc.MediaFileId == 0)
        {
            return null;
        }

        return BuildDocument(fileReferenceHelper, doc);
    }

    /// <summary>
    /// Parses the input privacy rules of stories.sendStory/editStory into their stored form, keeping
    /// every listed user/chat (not just the first).
    /// </summary>
    public static List<StoryPrivacyRule> ParsePrivacyRules(IEnumerable<IInputPrivacyRule>? rules)
    {
        var result = new List<StoryPrivacyRule>();
        if (rules == null)
        {
            return result;
        }

        foreach (var rule in rules)
        {
            switch (rule)
            {
                case TInputPrivacyValueAllowAll:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowAll });
                    break;
                case TInputPrivacyValueAllowContacts:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowContacts });
                    break;
                case TInputPrivacyValueDisallowAll:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.DisallowAll });
                    break;
                case TInputPrivacyValueDisallowContacts:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.DisallowContacts });
                    break;
                case TInputPrivacyValueAllowCloseFriends:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowCloseFriends });
                    break;
                case TInputPrivacyValueAllowPremium:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowPremium });
                    break;
                case TInputPrivacyValueAllowBots:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.AllowBots });
                    break;
                case TInputPrivacyValueDisallowBots:
                    result.Add(new StoryPrivacyRule { Type = StoryPrivacyRuleType.DisallowBots });
                    break;
                case TInputPrivacyValueAllowUsers allowUsers:
                    result.Add(new StoryPrivacyRule
                    {
                        Type = StoryPrivacyRuleType.AllowUsers,
                        UserIds = ExtractUserIds(allowUsers.Users)
                    });
                    break;
                case TInputPrivacyValueDisallowUsers disallowUsers:
                    result.Add(new StoryPrivacyRule
                    {
                        Type = StoryPrivacyRuleType.DisallowUsers,
                        UserIds = ExtractUserIds(disallowUsers.Users)
                    });
                    break;
                case TInputPrivacyValueAllowChatParticipants allowChats:
                    result.Add(new StoryPrivacyRule
                    {
                        Type = StoryPrivacyRuleType.AllowChatParticipants,
                        ChatIds = allowChats.Chats?.ToList() ?? []
                    });
                    break;
                case TInputPrivacyValueDisallowChatParticipants disallowChats:
                    result.Add(new StoryPrivacyRule
                    {
                        Type = StoryPrivacyRuleType.DisallowChatParticipants,
                        ChatIds = disallowChats.Chats?.ToList() ?? []
                    });
                    break;
            }
        }

        return result;
    }

    private static List<long> ExtractUserIds(IEnumerable<IInputUser>? users)
    {
        var result = new List<long>();
        if (users == null)
        {
            return result;
        }

        foreach (var user in users)
        {
            switch (user)
            {
                case TInputUser inputUser:
                    result.Add(inputUser.UserId);
                    break;
                case TInputUserFromMessage fromMessage:
                    result.Add(fromMessage.UserId);
                    break;
            }
        }

        return result;
    }

    public static TVector<IPrivacyRule> ConvertPrivacyRules(List<StoryPrivacyRule> rules)
    {
        var result = new TVector<IPrivacyRule>();

        foreach (var rule in rules)
        {
            switch (rule.Type)
            {
                case StoryPrivacyRuleType.AllowAll:
                    result.Add(new TPrivacyValueAllowAll());
                    break;
                case StoryPrivacyRuleType.AllowContacts:
                    result.Add(new TPrivacyValueAllowContacts());
                    break;
                case StoryPrivacyRuleType.DisallowAll:
                    result.Add(new TPrivacyValueDisallowAll());
                    break;
                case StoryPrivacyRuleType.DisallowContacts:
                    result.Add(new TPrivacyValueDisallowContacts());
                    break;
                case StoryPrivacyRuleType.AllowCloseFriends:
                    result.Add(new TPrivacyValueAllowCloseFriends());
                    break;
                case StoryPrivacyRuleType.AllowPremium:
                    result.Add(new TPrivacyValueAllowPremium());
                    break;
                case StoryPrivacyRuleType.AllowBots:
                    result.Add(new TPrivacyValueAllowBots());
                    break;
                case StoryPrivacyRuleType.DisallowBots:
                    result.Add(new TPrivacyValueDisallowBots());
                    break;
                case StoryPrivacyRuleType.AllowUsers:
                    if (rule.UserIds.Count > 0)
                    {
                        result.Add(new TPrivacyValueAllowUsers { Users = new TVector<long>(rule.UserIds) });
                    }
                    break;
                case StoryPrivacyRuleType.DisallowUsers:
                    if (rule.UserIds.Count > 0)
                    {
                        result.Add(new TPrivacyValueDisallowUsers { Users = new TVector<long>(rule.UserIds) });
                    }
                    break;
                case StoryPrivacyRuleType.AllowChatParticipants:
                    if (rule.ChatIds.Count > 0)
                    {
                        result.Add(new TPrivacyValueAllowChatParticipants { Chats = new TVector<long>(rule.ChatIds) });
                    }
                    break;
                case StoryPrivacyRuleType.DisallowChatParticipants:
                    if (rule.ChatIds.Count > 0)
                    {
                        result.Add(new TPrivacyValueDisallowChatParticipants { Chats = new TVector<long>(rule.ChatIds) });
                    }
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts normalized hashtags (lowercase, no leading '#', de-duplicated) from a story caption,
    /// for stories.searchPosts.
    /// </summary>
    public static List<string> ExtractHashtags(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in HashtagRegex().Matches(caption))
        {
            // The match includes the leading '#'.
            var tag = match.Value[1..].ToLowerInvariant();
            if (tag.Length > 0 && seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    /// <summary>Normalizes a search hashtag the same way <see cref="ExtractHashtags"/> stores them.</summary>
    public static string NormalizeHashtag(string? hashtag)
    {
        return string.IsNullOrWhiteSpace(hashtag)
            ? string.Empty
            : hashtag.Trim().TrimStart('#').ToLowerInvariant();
    }

    public static TVector<IMessageEntity>? ParseEntities(string? entitiesJson)
    {
        if (string.IsNullOrEmpty(entitiesJson))
        {
            return null;
        }

        try
        {
            var entitiesData = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(entitiesJson);
            if (entitiesData == null)
            {
                return null;
            }

            var result = new TVector<IMessageEntity>();
            foreach (var e in entitiesData)
            {
                if (!e.TryGetValue("constructorId", out var constructorIdElem))
                {
                    continue;
                }

                if (!e.TryGetValue("offset", out var offsetElem) || !e.TryGetValue("length", out var lengthElem))
                {
                    continue;
                }

                var constructorId = constructorIdElem.GetUInt32();
                var offset = offsetElem.GetInt32();
                var length = lengthElem.GetInt32();

                var entity = BuildEntity(constructorId, offset, length, e);
                if (entity != null)
                {
                    result.Add(entity);
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IMessageEntity? BuildEntity(
        uint constructorId,
        int offset,
        int length,
        Dictionary<string, JsonElement> data)
    {
        switch (constructorId)
        {
            case 0x76a6d327: // messageEntityTextUrl
                return data.TryGetValue("url", out var urlElem)
                    ? new TMessageEntityTextUrl { Offset = offset, Length = length, Url = urlElem.GetString() }
                    : null;

            case 0xdc7b1140: // messageEntityMentionName
                return data.TryGetValue("userId", out var userIdElem)
                    ? new TMessageEntityMentionName { Offset = offset, Length = length, UserId = userIdElem.GetInt64() }
                    : null;

            case 0x73924be0: // messageEntityPre
                var pre = new TMessageEntityPre { Offset = offset, Length = length };
                if (data.TryGetValue("language", out var langElem))
                {
                    pre.Language = langElem.GetString();
                }
                return pre;

            case 0xc8cf05f8: // messageEntityCustomEmoji
                return data.TryGetValue("documentId", out var documentIdElem)
                    ? new TMessageEntityCustomEmoji
                    {
                        Offset = offset,
                        Length = length,
                        DocumentId = documentIdElem.GetInt64()
                    }
                    : null;

            case 0xbd610bc9: return new TMessageEntityBold { Offset = offset, Length = length };
            case 0x826f8b60: return new TMessageEntityItalic { Offset = offset, Length = length };
            case 0x9c4e7e8b: return new TMessageEntityUnderline { Offset = offset, Length = length };
            case 0xbf0693d4: return new TMessageEntityStrike { Offset = offset, Length = length };
            case 0x28a20571: return new TMessageEntityCode { Offset = offset, Length = length };

            case 0xf1ccaaac: // messageEntityBlockquote
                return new TMessageEntityBlockquote
                {
                    Offset = offset,
                    Length = length,
                    Collapsed = data.TryGetValue("collapsed", out var collapsedElem) &&
                                collapsedElem.ValueKind == JsonValueKind.True
                };

            case 0x32ca960f: return new TMessageEntitySpoiler { Offset = offset, Length = length };
            case 0x6f635b0d: return new TMessageEntityHashtag { Offset = offset, Length = length };
            case 0xfa04579d: return new TMessageEntityMention { Offset = offset, Length = length };
            case 0x6ed02538: return new TMessageEntityUrl { Offset = offset, Length = length };
            case 0x64e475c2: return new TMessageEntityEmail { Offset = offset, Length = length };
            case 0x4c4e743f: return new TMessageEntityCashtag { Offset = offset, Length = length };
            case 0x6cef8ac7: return new TMessageEntityBotCommand { Offset = offset, Length = length };
            case 0x9b69e34b: return new TMessageEntityPhone { Offset = offset, Length = length };
            case 0x761e6af4: return new TMessageEntityBankCard { Offset = offset, Length = length };

            case 0x904ac7c7: // messageEntityFormattedDate
                return new TMessageEntityFormattedDate
                {
                    Offset = offset,
                    Length = length,
                    Date = data.TryGetValue("date", out var dateElem) ? dateElem.GetInt32() : 0,
                    Relative = GetFlag(data, "relative"),
                    ShortTime = GetFlag(data, "shortTime"),
                    LongTime = GetFlag(data, "longTime"),
                    ShortDate = GetFlag(data, "shortDate"),
                    LongDate = GetFlag(data, "longDate"),
                    DayOfWeek = GetFlag(data, "dayOfWeek")
                };

            default:
                return new TMessageEntityUnknown { Offset = offset, Length = length };
        }
    }

    private static bool GetFlag(Dictionary<string, JsonElement> data, string key)
    {
        return data.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Serializes message entities for storage. Mirrors <see cref="ParseEntities"/>.
    /// </summary>
    public static string? SerializeEntities(IEnumerable<IMessageEntity>? entities)
    {
        if (entities == null)
        {
            return null;
        }

        var list = new List<Dictionary<string, object?>>();

        foreach (var e in entities)
        {
            var data = new Dictionary<string, object?>
            {
                ["constructorId"] = e.ConstructorId,
                ["offset"] = e.Offset,
                ["length"] = e.Length
            };

            switch (e)
            {
                case TMessageEntityTextUrl textUrl:
                    data["url"] = textUrl.Url;
                    break;
                case TMessageEntityMentionName mentionName:
                    data["userId"] = mentionName.UserId;
                    break;
                case TMessageEntityPre pre:
                    data["language"] = pre.Language;
                    break;
                case TMessageEntityCustomEmoji customEmoji:
                    data["documentId"] = customEmoji.DocumentId;
                    break;
                case TMessageEntityBlockquote blockquote:
                    data["collapsed"] = blockquote.Collapsed;
                    break;
                case TMessageEntityFormattedDate formattedDate:
                    data["date"] = formattedDate.Date;
                    data["relative"] = formattedDate.Relative;
                    data["shortTime"] = formattedDate.ShortTime;
                    data["longTime"] = formattedDate.LongTime;
                    data["shortDate"] = formattedDate.ShortDate;
                    data["longDate"] = formattedDate.LongDate;
                    data["dayOfWeek"] = formattedDate.DayOfWeek;
                    break;
            }

            list.Add(data);
        }

        return list.Count > 0 ? JsonSerializer.Serialize(list) : null;
    }

    public static IPeer CreatePeer(int peerType, long peerId)
    {
        return peerType switch
        {
            PeerTypeChat => new TPeerChat { ChatId = peerId },
            PeerTypeChannel => new TPeerChannel { ChannelId = peerId },
            _ => new TPeerUser { UserId = peerId }
        };
    }

    private static IPeer? CreatePeerOrNull(int peerType, long peerId)
    {
        return peerType switch
        {
            PeerTypeUser => new TPeerUser { UserId = peerId },
            PeerTypeChat => new TPeerChat { ChatId = peerId },
            PeerTypeChannel => new TPeerChannel { ChannelId = peerId },
            _ => null
        };
    }

    /// <summary>
    /// Maps an <see cref="IInputPeer"/> to the stored owner-peer pair. This performs no access check —
    /// callers that mutate or read restricted data must go through <see cref="IStoryAccessService"/>.
    /// </summary>
    public static (long peerId, int peerType) ResolvePeer(IInputPeer? peer, long defaultUserId)
    {
        return peer switch
        {
            TInputPeerSelf => (defaultUserId, PeerTypeUser),
            TInputPeerUser userPeer => (userPeer.UserId, PeerTypeUser),
            TInputPeerUserFromMessage fromMessage => (fromMessage.UserId, PeerTypeUser),
            TInputPeerChannel channelPeer => (channelPeer.ChannelId, PeerTypeChannel),
            TInputPeerChannelFromMessage channelFromMessage => (channelFromMessage.ChannelId, PeerTypeChannel),
            TInputPeerChat chatPeer => (chatPeer.ChatId, PeerTypeChat),
            _ => (defaultUserId, PeerTypeUser)
        };
    }

    public static int ToStoryPeerType(PeerType peerType)
    {
        return peerType switch
        {
            PeerType.User or PeerType.Self => PeerTypeUser,
            PeerType.Chat => PeerTypeChat,
            PeerType.Channel => PeerTypeChannel,
            _ => -1
        };
    }

    public static PeerType ToPeerType(int storyPeerType)
    {
        return storyPeerType switch
        {
            PeerTypeChat => PeerType.Chat,
            PeerTypeChannel => PeerType.Channel,
            _ => PeerType.User
        };
    }

    /// <summary>
    /// Evaluates a story's privacy rules against a viewer.
    /// <para>
    /// Telegram semantics: allow-rules are additive and disallow-rules take precedence, so the whole
    /// rule set is examined rather than returning on the first match. A story with no rules is visible.
    /// </para>
    /// </summary>
    /// <param name="doc">The story being read.</param>
    /// <param name="requestingUserId">The viewer.</param>
    /// <param name="context">
    /// The viewer's relationship to the story owner — contacts and close-friends membership, loaded once
    /// per request by <see cref="IStoryAccessService.GetViewerContextAsync"/>.
    /// </param>
    public static bool CanViewStory(StoryDocument doc, long requestingUserId, StoryViewerContext context)
    {
        // The owner always sees their own stories.
        if (IsOwner(doc, requestingUserId))
        {
            return true;
        }

        // Channel/chat stories follow membership, which the caller has already established.
        if (doc.OwnerPeerType != PeerTypeUser)
        {
            return true;
        }

        var rules = doc.PrivacyRules;
        if (rules == null || rules.Count == 0)
        {
            return true;
        }

        var isContact = context.IsContactOf(doc.OwnerPeerId);
        var isCloseFriend = context.IsCloseFriendOf(doc.OwnerPeerId);

        var allowed = false;
        var hasAllowRule = false;

        foreach (var rule in rules)
        {
            switch (rule.Type)
            {
                // Disallow rules win outright. Each guarded case needs an unguarded counterpart below
                // it: a `when` that does not match falls through to the remaining cases, so without
                // one a non-excluded viewer would reach `default` and be treated as unevaluable.
                case StoryPrivacyRuleType.DisallowAll:
                    return false;
                case StoryPrivacyRuleType.DisallowContacts when isContact:
                    return false;
                case StoryPrivacyRuleType.DisallowUsers when rule.UserIds.Contains(requestingUserId):
                    return false;
                case StoryPrivacyRuleType.DisallowContacts:
                case StoryPrivacyRuleType.DisallowUsers:
                    // The viewer is not excluded by this rule; it contributes no permission either.
                    break;

                case StoryPrivacyRuleType.AllowAll:
                    hasAllowRule = true;
                    allowed = true;
                    break;
                case StoryPrivacyRuleType.AllowContacts:
                    hasAllowRule = true;
                    if (isContact)
                    {
                        allowed = true;
                    }
                    break;
                case StoryPrivacyRuleType.AllowCloseFriends:
                    hasAllowRule = true;
                    if (isCloseFriend)
                    {
                        allowed = true;
                    }
                    break;
                case StoryPrivacyRuleType.AllowUsers:
                    hasAllowRule = true;
                    if (rule.UserIds.Contains(requestingUserId))
                    {
                        allowed = true;
                    }
                    break;
                case StoryPrivacyRuleType.AllowPremium:
                    hasAllowRule = true;
                    if (context.IsPremium)
                    {
                        allowed = true;
                    }
                    break;

                case StoryPrivacyRuleType.DisallowBots when context.IsBot:
                    return false;
                case StoryPrivacyRuleType.DisallowBots:
                    break;
                case StoryPrivacyRuleType.AllowBots:
                    hasAllowRule = true;
                    if (context.IsBot)
                    {
                        allowed = true;
                    }
                    break;

                // Chat-participant rules cannot be evaluated: basic-group membership is not queryable
                // here (GetChatMemberListQuery has no handler), so there is no way to tell whether the
                // viewer is in the listed chats. They are therefore applied conservatively rather than
                // ignored -- previously neither had a case, so a story restricted to chat participants
                // matched no allow-rule and fell through to "no allow rule present => visible to all".
                // This denies some legitimate viewers, which is the safe direction; resolving group
                // membership into StoryViewerContext would make them exact.
                case StoryPrivacyRuleType.DisallowChatParticipants:
                    return false;
                case StoryPrivacyRuleType.AllowChatParticipants:
                    hasAllowRule = true;
                    break;

                default:
                    // An unrecognised rule type is treated as a restriction, never as permission.
                    hasAllowRule = true;
                    break;
            }
        }

        // Only disallow rules were present: everyone not explicitly excluded may view.
        return !hasAllowRule || allowed;
    }
}
