using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Obtain all chat themes associated to owned collectible gifts.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getUniqueGiftChatThemes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Returns chat themes based on owned unique star gifts with theme_available flag
/// </remarks>
internal sealed class GetUniqueGiftChatThemesHandler(
    IMongoDatabase database,
    IUserAppService userAppService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetUniqueGiftChatThemes, MyTelegram.Schema.Account.IChatThemes>
{
    protected override async Task<MyTelegram.Schema.Account.IChatThemes> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Account.RequestGetUniqueGiftChatThemes obj)
    {
        // Get user's unique star gifts with theme_available flag
        var savedGiftsCol = database.GetCollection<BsonDocument>("saved-star-gifts");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("IsUpgraded", true),
            Builders<BsonDocument>.Filter.Eq("ThemeAvailable", true)
        );

        var savedGifts = await savedGiftsCol.Find(filter).ToListAsync();

        if (savedGifts.Count == 0)
        {
            return new MyTelegram.Schema.Account.TChatThemesNotModified();
        }

        var themes = new TVector<MyTelegram.Schema.IChatTheme>();
        var chats = new TVector<MyTelegram.Schema.IChat>();
        var users = new TVector<MyTelegram.Schema.IUser>();
        var userIds = new HashSet<long>();

        // Load unique gift details
        var uniqueGiftsCol = database.GetCollection<BsonDocument>("unique-star-gifts");

        foreach (var savedGift in savedGifts)
        {
            var uniqueGiftId = savedGift["UniqueGiftId"].AsInt64;
            var uniqueGiftDoc = await uniqueGiftsCol.Find(
                Builders<BsonDocument>.Filter.Eq("UniqueGiftId", uniqueGiftId)
            ).FirstOrDefaultAsync();

            if (uniqueGiftDoc == null) continue;

            // Build StarGiftUnique
            var starGift = BuildStarGiftUnique(uniqueGiftDoc, savedGift, userIds);

            // Load theme settings
            var themeSettings = LoadThemeSettings(uniqueGiftDoc);

            themes.Add(new MyTelegram.Schema.TChatThemeUniqueGift
            {
                Gift = starGift,
                ThemeSettings = themeSettings
            });
        }

        // Load users
        foreach (var userId in userIds)
        {
            var userReadModel = await userAppService.GetAsync(userId);
            if (userReadModel != null)
            {
                users.Add(new MyTelegram.Schema.TUser
                {
                    Id = userReadModel.UserId,
                    AccessHash = userReadModel.AccessHash,
                    FirstName = userReadModel.FirstName ?? "",
                    LastName = userReadModel.LastName,
                    Username = userReadModel.UserName,
                    Phone = userReadModel.PhoneNumber,
                    Photo = new MyTelegram.Schema.TUserProfilePhotoEmpty()
                });
            }
        }

        return new MyTelegram.Schema.Account.TChatThemes
        {
            Hash = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Themes = themes,
            Chats = chats,
            Users = users
        };
    }

    private MyTelegram.Schema.IStarGift BuildStarGiftUnique(
        BsonDocument uniqueGiftDoc,
        BsonDocument savedGiftDoc,
        HashSet<long> userIds)
    {
        var attributes = new TVector<MyTelegram.Schema.IStarGiftAttribute>();

        if (uniqueGiftDoc.Contains("Attributes") && uniqueGiftDoc["Attributes"].IsBsonArray)
        {
            foreach (var attr in uniqueGiftDoc["Attributes"].AsBsonArray)
            {
                var attrDoc = attr.AsBsonDocument;
                var attrType = attrDoc["Type"].AsString;

                switch (attrType)
                {
                    case "model":
                        attributes.Add(new MyTelegram.Schema.TStarGiftAttributeModel
                        {
                            Name = attrDoc["Name"].AsString
                        });
                        break;
                    case "pattern":
                        attributes.Add(new MyTelegram.Schema.TStarGiftAttributePattern
                        {
                            Name = attrDoc["Name"].AsString
                        });
                        break;
                    case "backdrop":
                        attributes.Add(new MyTelegram.Schema.TStarGiftAttributeBackdrop
                        {
                            Name = attrDoc["Name"].AsString,
                            CenterColor = attrDoc.Contains("CenterColor") ? attrDoc["CenterColor"].AsInt32 : 0,
                            EdgeColor = attrDoc.Contains("EdgeColor") ? attrDoc["EdgeColor"].AsInt32 : 0,
                            PatternColor = attrDoc.Contains("PatternColor") ? attrDoc["PatternColor"].AsInt32 : 0,
                            TextColor = attrDoc.Contains("TextColor") ? attrDoc["TextColor"].AsInt32 : 0
                        });
                        break;
                }
            }
        }

        return new MyTelegram.Schema.TStarGiftUnique
        {
            ThemeAvailable = true,
            Id = uniqueGiftDoc["UniqueGiftId"].ToInt64(),
            GiftId = uniqueGiftDoc["GiftId"].ToInt64(),
            Title = uniqueGiftDoc["Title"].AsString,
            Slug = uniqueGiftDoc["Slug"].AsString,
            Num = uniqueGiftDoc["Number"].AsInt32,
            OwnerId = new MyTelegram.Schema.TPeerUser { UserId = savedGiftDoc["OwnerUserId"].ToInt64() },
            Attributes = attributes,
            AvailabilityIssued = uniqueGiftDoc["AvailabilityIssued"].AsInt32,
            AvailabilityTotal = uniqueGiftDoc["AvailabilityTotal"].AsInt32
        };
    }

    private TVector<MyTelegram.Schema.IThemeSettings> LoadThemeSettings(BsonDocument uniqueGiftDoc)
    {
        var settings = new TVector<MyTelegram.Schema.IThemeSettings>();

        if (uniqueGiftDoc.Contains("ThemeSettings") && uniqueGiftDoc["ThemeSettings"].IsBsonArray)
        {
            foreach (var settingDoc in uniqueGiftDoc["ThemeSettings"].AsBsonArray)
            {
                var setting = settingDoc.AsBsonDocument;
                var baseTheme = setting["BaseTheme"].AsString;

                var themeSettings = new MyTelegram.Schema.TThemeSettings
                {
                    BaseTheme = baseTheme == "classic"
                        ? new MyTelegram.Schema.TBaseThemeClassic()
                        : new MyTelegram.Schema.TBaseThemeNight(),
                    AccentColor = setting["AccentColor"].ToInt32(),
                    MessageColorsAnimated = setting.Contains("MessageColorsAnimated") && setting["MessageColorsAnimated"].AsBoolean,
                    MessageColors = new TVector<int>(
                        setting["MessageColors"].AsBsonArray.Select(c => c.ToInt32())
                    )
                };

                if (setting.Contains("OutboxAccentColor"))
                {
                    themeSettings.OutboxAccentColor = setting["OutboxAccentColor"].ToInt32();
                }

                if (setting.Contains("Wallpaper"))
                {
                    var wp = setting["Wallpaper"].AsBsonDocument;
                    var wpSettings = wp["Settings"].AsBsonDocument;

                    themeSettings.Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
                    {
                        Id = wp["Id"].ToInt64(),
                        Dark = wp.Contains("Dark") && wp["Dark"].AsBoolean,
                        Settings = new MyTelegram.Schema.TWallPaperSettings
                        {
                            BackgroundColor = wpSettings["BackgroundColor"].ToInt32(),
                            Intensity = wpSettings["Intensity"].ToInt32()
                        }
                    };
                }

                settings.Add(themeSettings);
            }
        }
        else
        {
            // Generate default theme from backdrop attribute
            var backdropAttr = uniqueGiftDoc.Contains("Attributes") && uniqueGiftDoc["Attributes"].IsBsonArray
                ? uniqueGiftDoc["Attributes"].AsBsonArray
                    .FirstOrDefault(a => a.AsBsonDocument["Type"].AsString == "backdrop")?.AsBsonDocument
                : null;

            if (backdropAttr != null)
            {
                var centerColor = backdropAttr.Contains("CenterColor") ? backdropAttr["CenterColor"].AsInt32 : 0x3390ec;
                var edgeColor = backdropAttr.Contains("EdgeColor") ? backdropAttr["EdgeColor"].AsInt32 : 0x6fb1f6;

                settings.Add(new MyTelegram.Schema.TThemeSettings
                {
                    BaseTheme = new MyTelegram.Schema.TBaseThemeClassic(),
                    AccentColor = centerColor,
                    OutboxAccentColor = edgeColor,
                    MessageColorsAnimated = true,
                    MessageColors = new TVector<int> { centerColor, edgeColor },
                    Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
                    {
                        Id = 0,
                        Settings = new MyTelegram.Schema.TWallPaperSettings { BackgroundColor = unchecked((int)0xFFFFFFFF), Intensity = 0 }
                    }
                });

                settings.Add(new MyTelegram.Schema.TThemeSettings
                {
                    BaseTheme = new MyTelegram.Schema.TBaseThemeNight(),
                    AccentColor = DarkenColor(centerColor),
                    MessageColorsAnimated = true,
                    MessageColors = new TVector<int> { DarkenColor(centerColor), DarkenColor(edgeColor) },
                    Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
                    {
                        Id = 0,
                        Dark = true,
                        Settings = new MyTelegram.Schema.TWallPaperSettings { BackgroundColor = unchecked((int)0xFF0F0F0F), Intensity = 0 }
                    }
                });
            }
        }

        return settings;
    }

    private static int DarkenColor(int color)
    {
        var r = (int)(((color >> 16) & 0xFF) * 0.8);
        var g = (int)(((color >> 8) & 0xFF) * 0.8);
        var b = (int)((color & 0xFF) * 0.8);
        return (r << 16) | (g << 8) | b;
    }
}
