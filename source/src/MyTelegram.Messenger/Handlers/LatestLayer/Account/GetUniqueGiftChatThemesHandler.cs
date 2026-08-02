using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

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
        // Get user's unique star gifts
        var savedGiftsCol = database.GetCollection<BsonDocument>("saved-star-gifts");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("IsUnique", true)
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
        var giftTypesCol = database.GetCollection<BsonDocument>("star-gifts");

        // Batch-load the gift-type (star-gifts) docs for every NFT the user owns.
        // The theme is owned by the gift type, so all NFTs of a gift — including
        // ones upgraded after the theme was released — inherit it automatically.
        var giftIds = savedGifts
            .Where(s => s.Contains("GiftId"))
            .Select(s => s["GiftId"].ToInt64())
            .Distinct()
            .ToList();
        var giftTypeById = new Dictionary<long, BsonDocument>();
        if (giftIds.Count > 0)
        {
            var giftTypes = await giftTypesCol.Find(
                Builders<BsonDocument>.Filter.In("GiftId", giftIds)
            ).ToListAsync();
            giftTypeById = giftTypes.ToDictionary(g => g["GiftId"].ToInt64());
        }

        foreach (var savedGift in savedGifts)
        {
            // saved-star-gifts stores the unique collectible id in RandomId
            // (RandomId = uniqueDoc.UniqueId, see UpgradeStarGiftHandler).
            if (!savedGift.Contains("RandomId")) continue;
            var uniqueGiftId = savedGift["RandomId"].AsInt64;
            var uniqueGiftDoc = await uniqueGiftsCol.Find(
                Builders<BsonDocument>.Filter.Eq("UniqueId", uniqueGiftId)
            ).FirstOrDefaultAsync();

            if (uniqueGiftDoc == null) continue;

            var giftId = uniqueGiftDoc.Contains("GiftId") ? uniqueGiftDoc["GiftId"].ToInt64() : 0L;
            giftTypeById.TryGetValue(giftId, out var giftTypeDoc);

            // Only expose a theme when the gift type has one released
            // (or the legacy per-NFT theme exists).
            var themeSettings = StarGiftThemeHelper.LoadThemeSettings(uniqueGiftDoc, giftTypeDoc);
            if (themeSettings == null || themeSettings.Count == 0)
            {
                continue;
            }

            // Build StarGiftUnique
            var starGift = BuildStarGiftUnique(uniqueGiftDoc, savedGift, userIds);

            themes.Add(new MyTelegram.Schema.TChatThemeUniqueGift
            {
                Gift = starGift,
                ThemeSettings = themeSettings
            });
        }

        if (themes.Count == 0)
        {
            return new MyTelegram.Schema.Account.TChatThemesNotModified();
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
            Id = uniqueGiftDoc["UniqueId"].ToInt64(),
            GiftId = uniqueGiftDoc["GiftId"].ToInt64(),
            Title = uniqueGiftDoc["Title"].AsString,
            Slug = uniqueGiftDoc["Slug"].AsString,
            Num = uniqueGiftDoc["Num"].AsInt32,
            OwnerId = new MyTelegram.Schema.TPeerUser { UserId = savedGiftDoc["OwnerUserId"].ToInt64() },
            Attributes = attributes,
            AvailabilityIssued = uniqueGiftDoc["AvailabilityIssued"].AsInt32,
            AvailabilityTotal = uniqueGiftDoc["AvailabilityTotal"].AsInt32
        };
    }
}
