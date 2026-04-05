using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;
using TUserFull = MyTelegram.Schema.Users.TUserFull;
using Microsoft.Extensions.Logging;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Returns extended user info by ID.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getFullUser"/> </c></para>
/// </summary>
internal sealed class GetFullUserHandler(IPeerHelper peerHelper, IQueryProcessor queryProcessor, IUserConverterService userConverterService, ILayeredService<IPeerSettingsConverter> peerSettingsLayeredService, ILayeredService<IPeerNotifySettingsConverter> peerNotifySettingsLayeredService, IBlockCacheAppService blockCacheAppService, IAccessHashHelper accessHashHelper, IContactHelper contactHelper, IPeerSettingsAppService peerSettingsAppService, IPhotoAppService photoAppService, IUserAppService userAppService, IPrivacyAppService privacyAppService, IMongoDatabase mongoDatabase, ILogger<GetFullUserHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetFullUser, MyTelegram.Schema.Users.IUserFull>
{
    protected override async Task<MyTelegram.Schema.Users.IUserFull> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetFullUser obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Id);
        var selfUserId = input.UserId;
        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;
        var userReadModel = await userAppService.GetAsync(targetPeer.PeerId);
        if (userReadModel == null)
        {
            throw new RpcException(RpcErrors.RpcErrors400.UserIdInvalid);
        }

        var photoReadModels = await photoAppService.GetPhotosAsync(userReadModel);
        var privacyReadModels = await privacyAppService.GetPrivacyListAsync(targetUserId);
        var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(input.UserId, targetUserId));
        var myContactReadModel = contactReadModels?.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel = contactReadModels?.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);
        var peerNotifySettingsId = PeerNotifySettingsId.Create(selfUserId, targetPeer.PeerType, targetPeer.PeerId);
        var peerNotifySettingReadModel = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(peerNotifySettingsId.Value));
        var peerSettingReadModel = await peerSettingsAppService.GetPeerSettingsAsync(input.UserId, targetPeer.PeerId);
        var contactType = contactHelper.GetContactType(myContactReadModel, targetUserContactReadModel);
        var peerSettings = peerSettingsLayeredService.GetConverter(input.Layer).ToPeerSettings(input.UserId, targetPeer.PeerId, peerSettingReadModel, contactType);

        // Add business bot fields to peer settings
        await SetBusinessBotFieldsAsync(peerSettings, selfUserId, targetUserId);

        var peerNotifySettings = peerNotifySettingsLayeredService.GetConverter(input.Layer).ToPeerNotifySettings(peerNotifySettingReadModel?.NotifySettings ?? PeerNotifySettings.DefaultSettings);
        var userFull = userConverterService.ToUserFull(input, userReadModel, photoReadModels, contactReadModels, privacyReadModels, input.Layer);
        userFull.Settings = peerSettings;
        userFull.NotifySettings = peerNotifySettings;
        userFull.Blocked = await blockCacheAppService.IsBlockedAsync(input.UserId, targetPeer.PeerId);
        var user = userConverterService.ToUser(input, userReadModel, photoReadModels, myContactReadModel, targetUserContactReadModel, privacyReadModels, input.Layer);
        await SetPersonalChannelAsync(input, userReadModel, userFull);
        await SetCommonChatCountAsync(input, userReadModel, userFull);
        await SetStarGiftsCountAsync(targetUserId, userFull, input.UserId);
        await SetStarsRatingAsync(targetUserId, userFull, isSelf: input.UserId == targetUserId);
        await SetDisallowedGiftsAsync(targetUserId, userFull);
        await SetBotVerificationAsync(targetUserId, 0, userFull, user);
        await SetUserStoriesAsync(targetUserId, userFull, input.UserId);
        await SetSavedMusicAsync(targetUserId, userFull);
        await SetChatThemeAsync(input.UserId, targetUserId, userFull);

        // CRITICAL: Cast to concrete TUser for proper serialization
        // ILayeredUser interface doesn't serialize correctly in TVector
        var tUser = user as TUser;
        var usersList = new List<IUser>();
        if (tUser != null)
        {
            usersList.Add(tUser);
        }

        return new TUserFull
        {
            Chats = new TVector<IChat>(),
            FullUser = userFull,
            Users = new TVector<IUser>(usersList)
        };
    }
    
    private async Task SetUserStoriesAsync(long targetUserId, IUserFull userFull, long requesterId)
    {
        var storyCollection = mongoDatabase.GetCollection<StoryDocument>("stories");
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        logger.LogDebug("SetUserStoriesAsync: targetUserId={TargetUserId}, requesterId={RequesterId}, currentTime={CurrentTime}", targetUserId, requesterId, currentTime);
        
        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, targetUserId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, 0),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Eq(s => s.Archived, false),
            Builders<StoryDocument>.Filter.Lte(s => s.Date, currentTime),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );
        
        var stories = await storyCollection.Find(filter)
            .SortByDescending(s => s.StoryId)
            .ToListAsync();
        
        logger.LogDebug("SetUserStoriesAsync: Found {Count} stories for targetUserId={TargetUserId}", stories.Count, targetUserId);
        
        if (stories.Count > 0)
        {
            foreach (var story in stories)
            {
                logger.LogDebug("  Story: StoryId={StoryId}, Date={Date}, ExpireDate={ExpireDate}, OwnerPeerId={OwnerPeerId}, OwnerPeerType={OwnerPeerType}, Deleted={Deleted}, Archived={Archived}",
                    story.StoryId, story.Date, story.ExpireDate, story.OwnerPeerId, story.OwnerPeerType, story.Deleted, story.Archived);
            }
            
            var storyItems = new TVector<MyTelegram.Schema.IStoryItem>();
            foreach (var story in stories)
            {
                storyItems.Add(StoryHelper.ConvertToStoryItem(story, requesterId));
            }
            
            var peerStories = new MyTelegram.Schema.TPeerStories
            {
                Peer = new MyTelegram.Schema.TPeerUser { UserId = targetUserId },
                Stories = storyItems
            };
            
            userFull.Stories = peerStories;
            userFull.StoriesPinnedAvailable = stories.Any(s => s.Pinned);
            logger.LogDebug("SetUserStoriesAsync: Set Stories on userFull with {Count} story items", storyItems.Count);
        }
    }

    private async Task SetStarGiftsCountAsync(long userId, IUserFull userFull, long requesterId)
    {
        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var filter = Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, userId);
        if (userId != requesterId)
            filter &= Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Saved, true);
        var count = (int)await collection.CountDocumentsAsync(filter);
        if (count > 0)
            userFull.StargiftsCount = count;
    }

    // Levels calibrated for server scale: 1k → ~1B stars across levels 1-20
    private static readonly long[] RatingLevels = [0, 1_000, 3_000, 10_000, 30_000, 100_000, 300_000, 1_000_000, 3_000_000, 10_000_000, 30_000_000, 100_000_000, 300_000_000, 1_000_000_000];

    private static (int level, long current, long? next) CalcRatingLevel(long totalStars)
    {
        // Beyond the fixed table: each level multiplies threshold by 3
        long threshold = RatingLevels[^1]; // 50000
        int level = RatingLevels.Length - 1;
        while (totalStars >= threshold * 3)
        {
            threshold *= 3;
            level++;
        }
        if (totalStars < RatingLevels[^1])
        {
            for (int i = RatingLevels.Length - 1; i >= 0; i--)
                if (totalStars >= RatingLevels[i]) { level = i; break; }
            long current = RatingLevels[level];
            long? next = level + 1 < RatingLevels.Length ? RatingLevels[level + 1] : null;
            return (level, current, next);
        }
        return (level, threshold, threshold * 3);
    }

    private async Task SetStarsRatingAsync(long userId, IUserFull userFull, bool isSelf = false)
    {
        var userReadModel = await userAppService.GetAsync(userId);
        if (userReadModel?.Bot == true) return;
        // Check for rating override
        var overrideCollection = mongoDatabase.GetCollection<BsonDocument>("user-rating-overrides");
        var overrideDoc = await (await overrideCollection.FindAsync(new BsonDocument("UserId", userId))).FirstOrDefaultAsync();
        if (overrideDoc != null)
        {
            var overrideLevel = overrideDoc["Level"].ToInt32();
            userFull.StarsRating = new TStarsRating { Level = overrideLevel, CurrentLevelStars = 0, Stars = 0, NextLevelStars = null };
            return;
        }

        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                { "FromUserId", userId },
                { "IsAuction", new BsonDocument("$ne", true) }
            }),
            new BsonDocument("$group", new BsonDocument { { "_id", BsonNull.Value }, { "total", new BsonDocument("$sum", "$Stars") } })
        };
        var result = await collection.AggregateAsync<BsonDocument>(pipeline);
        var doc = await result.FirstOrDefaultAsync();
        var totalStars = doc != null && doc.Contains("total") ? doc["total"].ToInt64() : 0;
        if (totalStars <= 0) return;

        var (level, currentLevelStars, nextLevelStars) = CalcRatingLevel(totalStars);

        userFull.StarsRating = new TStarsRating
        {
            Level = level,
            CurrentLevelStars = currentLevelStars,
            Stars = totalStars,
            NextLevelStars = nextLevelStars ?? currentLevelStars * 3,
        };

        // Pending rating only visible to self
        if (isSelf)
        {
            var nextThreshold = nextLevelStars ?? currentLevelStars * 3;
            var (nextLevel, nextCurrent, nextNext) = CalcRatingLevel(nextThreshold);
            userFull.StarsMyPendingRating = new TStarsRating
            {
                Level = nextLevel,
                CurrentLevelStars = nextCurrent,
                Stars = nextThreshold,
                NextLevelStars = nextNext ?? nextCurrent * 3,
            };
            userFull.StarsMyPendingRatingDate = (int)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        }
    }

    private async Task SetDisallowedGiftsAsync(long userId, IUserFull userFull)
    {
        var gps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(userId));
        if (gps == null) return;
        if (gps.DisallowUnlimitedStargifts || gps.DisallowLimitedStargifts || gps.DisallowUniqueStargifts || gps.DisallowPremiumGifts)
        {
            userFull.DisallowedGifts = new TDisallowedGiftsSettings
            {
                DisallowUnlimitedStargifts = gps.DisallowUnlimitedStargifts,
                DisallowLimitedStargifts = gps.DisallowLimitedStargifts,
                DisallowUniqueStargifts = gps.DisallowUniqueStargifts,
                DisallowPremiumGifts = gps.DisallowPremiumGifts,
            };
        }
    }

    private async Task SetCommonChatCountAsync(IRequestInput input, IUserReadModel userReadModel, IUserFull userFull)
    {
        var count = await queryProcessor.ProcessAsync(new GetCommonChatCountQuery(input.UserId, userReadModel.UserId));
        userFull.CommonChatsCount = count;
    }

    private async Task SetPersonalChannelAsync(IRequestInput input, IUserReadModel userReadModel, IUserFull userFull)
    {
        if (userReadModel.PersonalChannelId.HasValue)
        {
            var channelTopMessageId = await queryProcessor.ProcessAsync(new GetChannelTopMessageIdQuery(userReadModel.PersonalChannelId.Value));
            if (channelTopMessageId.HasValue)
            {
                userFull.PersonalChannelId = userReadModel.PersonalChannelId;
                userFull.PersonalChannelMessage = channelTopMessageId.Value;
            }
        }
    }

    private async Task SetBotVerificationAsync(long userId, long channelId, IUserFull? userFull, ILayeredUser? user)
    {
        var col = mongoDatabase.GetCollection<MyTelegram.Messenger.Services.BotVerificationDocument>("bot-verifications");
        var filter = userId != 0
            ? Builders<MyTelegram.Messenger.Services.BotVerificationDocument>.Filter.Eq(x => x.UserId, userId)
            : Builders<MyTelegram.Messenger.Services.BotVerificationDocument>.Filter.Eq(x => x.ChannelId, channelId);
        var doc = await (await col.FindAsync(filter)).FirstOrDefaultAsync();
        if (doc == null) return;

        var bv = new TBotVerification { BotId = doc.BotId, Icon = doc.Icon, Description = doc.Description };
        if (userFull != null) userFull.BotVerification = bv;
        if (user is TUser tUser) tUser.BotVerificationIcon = doc.Icon;
    }

    private async Task SetBusinessBotFieldsAsync(Schema.IPeerSettings settings, long selfUserId, long targetUserId)
    {
        try
        {
            // ONLY show business bot banner in private chats with users (not in channels/groups/bots)
            // GetFullUser is ONLY called for users, so this check is redundant but kept for clarity
            var targetUser = await userAppService.GetAsync(targetUserId);
            if (targetUser == null)
            {
                // Not a user - don't show banner
                return;
            }

            // Find connected bot for the SELF user (who is opening the chat - bot owner)
            var collection = mongoDatabase.GetCollection<BsonDocument>("connected_business_bots");
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", selfUserId);
            var connection = await collection.Find(filter).FirstOrDefaultAsync();

            if (connection != null && settings is Schema.TPeerSettings tSettings)
            {
                var botId = connection["BotId"].AsInt64;
                var connectionId = connection.Contains("ConnectionId") ? connection["ConnectionId"].AsString : "";

                // Check recipients - should this chat be managed?
                var recipientsDoc = connection["Recipients"].AsBsonDocument;
                bool excludeSelected = recipientsDoc.Contains("ExcludeSelected") && recipientsDoc["ExcludeSelected"].AsBoolean;

                // Default: if bot is connected, manage all chats
                bool shouldManage = true;

                // Check explicit exclusion
                if (recipientsDoc.Contains("ExcludeUsers") && !recipientsDoc["ExcludeUsers"].IsBsonNull)
                {
                    var excludeArray = recipientsDoc["ExcludeUsers"].AsBsonArray;
                    if (excludeArray.Any(u => u.AsInt64 == targetUserId))
                    {
                        shouldManage = false;
                    }
                }

                // If explicit users list exists and not empty - only they see banner
                if (shouldManage &&
                    recipientsDoc.Contains("Users") &&
                    !recipientsDoc["Users"].IsBsonNull)
                {
                    var usersArray = recipientsDoc["Users"].AsBsonArray;
                    if (usersArray.Count > 0 && !usersArray.Any(u => u.AsInt64 == targetUserId))
                    {
                        shouldManage = false;
                    }
                }

                if (shouldManage)
                {
                    // Get bot user for username - show CONNECTED BOT username, not botfather
                    var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botId));
                    var manageUrl = botUser?.UserName != null
                        ? $"https://t.me/{botUser.UserName}?start=bizbot_{connectionId}"
                        : $"tg://resolve?domain=botfather&start=manage_{botId}";

                    // CRITICAL: Both business_bot_id and business_bot_manage_url must be set (flag 13)
                    tSettings.BusinessBotId = botId;
                    tSettings.BusinessBotManageUrl = manageUrl;

                    // Check if bot is paused in this specific chat
                    var pausedCollection = mongoDatabase.GetCollection<BsonDocument>("paused_business_bot_chats");
                    var pausedFilter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("UserId", selfUserId),
                        Builders<BsonDocument>.Filter.Eq("PeerId", targetUserId)
                    );
                    var pausedDoc = await pausedCollection.Find(pausedFilter).FirstOrDefaultAsync();
                    tSettings.BusinessBotPaused = pausedDoc != null && pausedDoc.Contains("Paused") && pausedDoc["Paused"].AsBoolean;

                    // Check bot rights - can it reply?
                    var rightsDoc = connection.Contains("Rights") ? connection["Rights"].AsBsonDocument : null;
                    tSettings.BusinessBotCanReply = rightsDoc != null && rightsDoc.Contains("Reply") && rightsDoc["Reply"].AsBoolean;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    private async Task SetSavedMusicAsync(long targetUserId, IUserFull userFull)
    {
        try
        {
            // Query saved music from MongoDB
            var collection = mongoDatabase.GetCollection<BsonDocument>("saved_music");
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", targetUserId);
            var sort = Builders<BsonDocument>.Sort.Ascending("Order"); // Sort by Order field

            // Get the first (lowest order = most recent) saved music document
            var savedMusicDoc = await collection.Find(filter)
                .Sort(sort)
                .Limit(1)
                .FirstOrDefaultAsync();

            if (savedMusicDoc != null)
            {
                var documentId = savedMusicDoc["DocumentId"].AsInt64;

                // Load document from eventflow-documentreadmodel
                var docCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
                var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", documentId);
                var docDoc = await docCollection.Find(docFilter).FirstOrDefaultAsync();

                if (docDoc != null)
                {
                    userFull.SavedMusic = ConvertToDocument(docDoc);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load saved music for user {UserId}", targetUserId);
        }
    }

    private static IDocument ConvertToDocument(BsonDocument doc)
    {
        var fileRef = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
            ? doc["FileReference"].AsBsonBinaryData.Bytes
            : Array.Empty<byte>();

        var attributes = new TVector<IDocumentAttribute>();

        // Use Attributes2 if available (proper serialization)
        if (doc.Contains("Attributes2") && !doc["Attributes2"].IsBsonNull)
        {
            var attrs2 = doc["Attributes2"].AsBsonArray;
            foreach (var attrBson in attrs2)
            {
                if (attrBson.IsBsonDocument)
                {
                    var attrDoc = attrBson.AsBsonDocument;
                    var typeName = attrDoc["_t"].AsString;

                    // Handle audio attributes
                    if (typeName.EndsWith("TDocumentAttributeAudio"))
                    {
                        var audioAttr = new TDocumentAttributeAudio
                        {
                            Voice = attrDoc.Contains("Voice") && attrDoc["Voice"].AsBoolean,
                            Duration = attrDoc.Contains("Duration") ? attrDoc["Duration"].AsInt32 : 0,
                            Title = attrDoc.Contains("Title") ? attrDoc["Title"].AsString : null,
                            Performer = attrDoc.Contains("Performer") ? attrDoc["Performer"].AsString : null,
                            Waveform = attrDoc.Contains("Waveform") && !attrDoc["Waveform"].IsBsonNull
                                ? attrDoc["Waveform"].AsByteArray : null
                        };
                        attributes.Add(audioAttr);
                    }
                    // Handle filename attributes
                    else if (typeName.EndsWith("TDocumentAttributeFilename"))
                    {
                        attributes.Add(new TDocumentAttributeFilename
                        {
                            FileName = attrDoc.Contains("FileName") ? attrDoc["FileName"].AsString : ""
                        });
                    }
                }
            }
        }

        return new TDocument
        {
            Id = doc["DocumentId"].AsInt64,
            AccessHash = doc["AccessHash"].AsInt64,
            FileReference = fileRef,
            Date = doc["Date"].AsInt32,
            MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "audio/mpeg",
            Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2,
            Attributes = attributes
        };
    }

    private async Task SetChatThemeAsync(long selfUserId, long targetUserId, IUserFull userFull)
    {
        try
        {
            // Load chat theme from MongoDB
            var collection = mongoDatabase.GetCollection<BsonDocument>("user_chat_themes");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", selfUserId),
                Builders<BsonDocument>.Filter.Eq("PeerType", 0), // PeerType.User
                Builders<BsonDocument>.Filter.Eq("PeerId", targetUserId)
            );

            var themeDoc = await collection.Find(filter).FirstOrDefaultAsync();
            if (themeDoc != null)
            {
                // Check if it's an emoji theme or gift theme
                if (themeDoc.Contains("Emoticon") && !string.IsNullOrEmpty(themeDoc["Emoticon"].AsString))
                {
                    userFull.Theme = new MyTelegram.Schema.TChatTheme
                    {
                        Emoticon = themeDoc["Emoticon"].AsString
                    };
                }
                else if (themeDoc.Contains("GiftSlug") && !string.IsNullOrEmpty(themeDoc["GiftSlug"].AsString))
                {
                    // Load unique gift theme
                    var giftSlug = themeDoc["GiftSlug"].AsString;
                    var uniqueGiftsCol = mongoDatabase.GetCollection<BsonDocument>("unique-star-gifts");
                    var giftDoc = await uniqueGiftsCol.Find(
                        Builders<BsonDocument>.Filter.Eq("Slug", giftSlug)
                    ).FirstOrDefaultAsync();

                    if (giftDoc != null)
                    {
                        var starGift = new MyTelegram.Schema.TStarGiftUnique
                        {
                            ThemeAvailable = true,
                            Id = giftDoc["UniqueGiftId"].ToInt64(),
                            GiftId = giftDoc["GiftId"].ToInt64(),
                            Title = giftDoc["Title"].AsString,
                            Slug = giftDoc["Slug"].AsString,
                            Num = giftDoc["Number"].AsInt32,
                            Attributes = new TVector<MyTelegram.Schema.IStarGiftAttribute>(),
                            AvailabilityIssued = giftDoc["AvailabilityIssued"].AsInt32,
                            AvailabilityTotal = giftDoc["AvailabilityTotal"].AsInt32
                        };

                        var themeSettings = new TVector<MyTelegram.Schema.IThemeSettings>();
                        if (giftDoc.Contains("ThemeSettings") && giftDoc["ThemeSettings"].IsBsonArray)
                        {
                            foreach (var settingDoc in giftDoc["ThemeSettings"].AsBsonArray)
                            {
                                var setting = settingDoc.AsBsonDocument;
                                themeSettings.Add(new MyTelegram.Schema.TThemeSettings
                                {
                                    BaseTheme = setting["BaseTheme"].AsString == "classic"
                                        ? new MyTelegram.Schema.TBaseThemeClassic()
                                        : new MyTelegram.Schema.TBaseThemeNight(),
                                    AccentColor = setting["AccentColor"].ToInt32(),
                                    MessageColorsAnimated = true,
                                    MessageColors = new TVector<int>(setting["MessageColors"].AsBsonArray.Select(c => c.ToInt32()))
                                });
                            }
                        }

                        userFull.Theme = new MyTelegram.Schema.TChatThemeUniqueGift
                        {
                            Gift = starGift,
                            ThemeSettings = themeSettings
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load chat theme for user {SelfUserId} -> {TargetUserId}", selfUserId, targetUserId);
        }
    }
}
