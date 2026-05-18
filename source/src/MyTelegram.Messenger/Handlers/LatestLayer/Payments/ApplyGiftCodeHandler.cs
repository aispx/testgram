using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Apply a <a href="https://corefork.telegram.org/api/giveaways">Telegram Premium giftcode »</a>
/// Possible errors
/// Code Type Description
/// 400 GIFT_SLUG_EXPIRED The specified gift slug has expired.
/// 400 GIFT_SLUG_INVALID The specified slug is invalid.
/// 420 PREMIUM_SUB_ACTIVE_UNTIL_%d You already have a premium subscription active until unixtime %d .
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.applyGiftCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ApplyGiftCodeHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestApplyGiftCode, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestApplyGiftCode obj)
    {
        var codeCol = mongoDatabase.GetCollection<BsonDocument>("premium_gift_codes");
        var filter = Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug);
        var codeDoc = await codeCol.Find(filter).FirstOrDefaultAsync();

        if (codeDoc == null)
            RpcErrors.RpcErrors400.SlugInvalid.ThrowRpcError();

        if (codeDoc.Contains("Used") && codeDoc["Used"].AsBoolean)
            RpcErrors.RpcErrors400.SlugInvalid.ThrowRpcError();

        // Check if code is for specific user
        var toId = codeDoc.Contains("ToId") && !codeDoc["ToId"].IsBsonNull ? codeDoc["ToId"].AsInt64 : (long?)null;

        if (toId.HasValue && toId.Value != input.UserId)
        {
            // Private gift code - only specific user can use
            RpcErrors.RpcErrors400.SlugInvalid.ThrowRpcError();
        }

        // Get months or stars from code
        int months = codeDoc.Contains("Months") ? codeDoc["Months"].AsInt32 : 1;
        var stars = codeDoc.Contains("Stars") && !codeDoc["Stars"].IsBsonNull ? codeDoc["Stars"].AsInt64 : (long?)null;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Premium subscription - check if user already has active Premium BEFORE marking the slug as used,
        // so a wasted activation does not happen when the user already has Premium.
        if (!stars.HasValue)
        {
            var subCol = mongoDatabase.GetCollection<BsonDocument>("premium_subscriptions");
            var existingSub = await subCol.Find(
                Builders<BsonDocument>.Filter.Eq("_id", $"premium-{input.UserId}")
            ).FirstOrDefaultAsync();

            if (existingSub != null &&
                existingSub.Contains("Active") && existingSub["Active"].AsBoolean &&
                existingSub.Contains("ExpiresAt") && existingSub["ExpiresAt"].AsInt32 > now)
            {
                // User already has active Premium - throw error with expiration date.
                // The gift code slug is NOT marked as used, so it remains valid for a different account.
                var existingExpiresAt = existingSub["ExpiresAt"].AsInt32;
                RpcErrors.RpcErrors420.PremiumSubActiveUntilX.ThrowRpcError(existingExpiresAt);
            }
        }

        // Atomically mark code as used (also handles race condition for public codes)
        var atomicUpdate = Builders<BsonDocument>.Update
            .Set("Used", true)
            .Set("UsedBy", input.UserId)
            .Set("UsedDate", now);

        var atomicFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Exists("Used", false),
                Builders<BsonDocument>.Filter.Eq("Used", false)
            )
        );

        var result = await codeCol.UpdateOneAsync(atomicFilter, atomicUpdate);

        if (result.MatchedCount == 0)
        {
            // Code was already used by someone else
            RpcErrors.RpcErrors400.SlugInvalid.ThrowRpcError();
        }

        if (stars.HasValue)
        {
            // Add stars to user balance
            var starsCol = mongoDatabase.GetCollection<BsonDocument>("star-balances");
            await starsCol.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", $"balance-{input.UserId}"),
                Builders<BsonDocument>.Update.Inc("Balance", stars.Value),
                new UpdateOptions { IsUpsert = true }
            );

            // Create transaction
            var transCol = mongoDatabase.GetCollection<BsonDocument>("star-transactions");
            await transCol.InsertOneAsync(new BsonDocument
            {
                ["_id"] = $"trans-{Guid.NewGuid()}",
                ["UserId"] = input.UserId,
                ["Amount"] = stars.Value,
                ["Date"] = now,
                ["Gift"] = true,
                ["Title"] = "Gift Code"
            });
        }
        else
        {
            // No active Premium - activate subscription
            var subCol = mongoDatabase.GetCollection<BsonDocument>("premium_subscriptions");
            var expiresAt = now + (months * 30 * 24 * 3600);

            // Create premium subscription
            await subCol.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", $"premium-{input.UserId}"),
                new BsonDocument
                {
                    ["_id"] = $"premium-{input.UserId}",
                    ["UserId"] = input.UserId,
                    ["Active"] = true,
                    ["ExpiresAt"] = expiresAt,
                    ["Months"] = months,
                    ["GrantedAt"] = now,
                    ["GrantedBy"] = "giftcode"
                },
                new ReplaceOptions { IsUpsert = true }
            );

            // Update user Premium flag
            var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
            await userCol.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                Builders<BsonDocument>.Update.Set("Premium", true)
            );

            // Grant boost slots (1 for recipient)
            await GrantBoostSlotsAsync(input.UserId, 1);
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = now,
            Seq = 0
        };
    }

    private async Task GrantBoostSlotsAsync(long userId, int count)
    {
        var boostSlotCol = mongoDatabase.GetCollection<BsonDocument>("user-boost-slots");
        var existingSlots = await boostSlotCol.Find(
            Builders<BsonDocument>.Filter.Eq("UserId", userId)
        ).ToListAsync();

        var usedSlots = existingSlots.Select(b => b["Slot"].AsInt32).ToHashSet();

        // Find available slot numbers
        var slotsToCreate = new List<int>();
        int slot = 1;
        while (slotsToCreate.Count < count)
        {
            if (!usedSlots.Contains(slot))
                slotsToCreate.Add(slot);
            slot++;
        }

        // Insert new slots
        foreach (var slotNum in slotsToCreate)
        {
            await boostSlotCol.InsertOneAsync(new BsonDocument
            {
                ["_id"] = $"boost-slot-{userId}-{slotNum}",
                ["UserId"] = userId,
                ["Slot"] = slotNum,
                ["Cooldown"] = 0,
                ["ChannelId"] = BsonNull.Value
            });
        }
    }
}