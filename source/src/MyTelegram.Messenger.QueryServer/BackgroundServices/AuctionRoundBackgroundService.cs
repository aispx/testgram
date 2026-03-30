using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

public class AuctionRoundBackgroundService(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    ILogger<AuctionRoundBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessExpiredRoundsAsync(); } catch (Exception ex) { logger.LogError(ex, "AuctionRound error"); }
            await Task.Delay(10_000, stoppingToken);
        }
    }

    private async Task ProcessExpiredRoundsAsync()
    {
        var now = DateTime.UtcNow.ToTimestamp();
        var auctionToProcessCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctionToProcesss");
        var acquiredCol = mongoDatabase.GetCollection<AuctionAcquiredGiftDocument>("star-gift-auctionToProcess-acquired");
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        // Find all non-finished auctionToProcesss and then check conditions in code
        var allActiveAuctions = await auctionToProcessCol.Find(a => !a.Finished).ToListAsync();
        
        var expiredAuctions = allActiveAuctions.Where(a =>
            (a.NextRoundAt > 0 && a.NextRoundAt <= now) ||
            (a.NextRoundAt == 0 && (a.CurrentRound >= a.TotalRounds || a.GiftsLeft <= 0 || a.CurrentRound > a.TotalRounds))
        ).ToList();

        if (expiredAuctions.Count > 0)
        {
            logger.LogWarning("Found {Count} expired auctionToProcesss to process", expiredAuctions.Count);
            foreach (var a in expiredAuctions)
            {
                logger.LogWarning("Processing auctionToProcess {GiftId} round={CurrentRound}/{TotalRounds} NextRoundAt={NextRoundAt} GiftsLeft={GiftsLeft}",
                    a.GiftId, a.CurrentRound, a.TotalRounds, a.NextRoundAt, a.GiftsLeft);
            }
        }

        var auctionCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
        foreach (var auctionItem in expiredAuctions)
        {
            try
            {
                // Get fresh auction state from DB to avoid stale data issues
                var auctionToProcess = await auctionCol.Find(a => a.Id == auctionItem.Id).FirstOrDefaultAsync();
                if (auctionToProcess == null || auctionToProcess.Finished) continue;
                if (auctionToProcess.NextRoundAt > 0 && auctionToProcess.NextRoundAt > now) continue;
            
            var giftDoc = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
                .Find(g => g.GiftId == auctionToProcess.GiftId).FirstOrDefaultAsync();

            // Top bidders for this round
            var topBids = auctionToProcess.Bids
                .Where(b => !b.Returned)
                .GroupBy(b => b.UserId)
                .Select(g => g.OrderByDescending(b => b.Amount).First())
                .OrderByDescending(b => b.Amount)
                .Take(auctionToProcess.GiftsPerRound)
                .ToList();

            int pos = 1;
            long totalBid = 0;
            var winnerIds = new HashSet<long>();
            int distributedBefore = (auctionToProcess.AvailabilityTotal ?? auctionToProcess.GiftsPerRound * auctionToProcess.TotalRounds) - auctionToProcess.GiftsLeft;

            foreach (var bid in topBids)
            {
                winnerIds.Add(bid.UserId);
                totalBid += bid.Amount;
                int giftNum = distributedBefore + pos;

                await acquiredCol.InsertOneAsync(new AuctionAcquiredGiftDocument
                {
                    GiftId = auctionToProcess.GiftId,
                    UserId = bid.UserId,
                    Date = now,
                    BidAmount = bid.Amount,
                    Round = auctionToProcess.CurrentRound,
                    Pos = pos,
                    GiftNum = giftNum,
                    NameHidden = bid.NameHidden,
                    MessageText = bid.MessageText,
                });
                
                pos++;

                // Save gift to winner's profile
                if (giftDoc != null)
                {
                    // Use unique randomId: combine round, userId, giftNum and random to prevent duplicates
                    var uniqueSalt = (long)(Guid.NewGuid().GetHashCode() & 0xFFFFFFFF);
                    var randomId = (long)(((ulong)auctionToProcess.CurrentRound << 48) | (((ulong)giftNum & 0xFFFF) << 32) | (((ulong)bid.UserId & 0xFFFFFFFF) & 0xFFFFFFFF) | (ulong)uniqueSalt);
                    
                    // Check if already saved for this gift
                    var existing = await savedCol.Find(s => s.OwnerUserId == bid.UserId && s.GiftId == auctionToProcess.GiftId && s.GiftNum == giftNum).FirstOrDefaultAsync();
                    if (existing != null)
                    {
                        logger.LogInformation("Gift already saved for user {UserId} gift {GiftId} giftNum {GiftNum}", bid.UserId, auctionToProcess.GiftId, giftNum);
                        continue;
                    }
                    
                    await savedCol.InsertOneAsync(new SavedStarGiftDocument
                    {
                        OwnerUserId = bid.UserId,
                        FromUserId = bid.UserId, // for rating
                        IsAuction = true,
                        GiftNum = giftNum,
                        GiftId = giftDoc.GiftId,
                        Stars = bid.Amount, // stars paid by winner
                        ConvertStars = giftDoc.ConvertStars,
                        UpgradeStars = giftDoc.UpgradeStars,
                        NameHidden = bid.NameHidden,
                        Saved = true,
                        Date = now,
                        MessageText = bid.MessageText,
                        RandomId = randomId,
                        DocumentId = giftDoc.DocumentId,
                        DocumentAccessHash = giftDoc.DocumentAccessHash,
                        FileReference = giftDoc.FileReference,
                        DocumentDate = giftDoc.DocumentDate,
                        MimeType = giftDoc.MimeType,
                        DocumentSize = giftDoc.DocumentSize,
                        DcId = giftDoc.DcId,
                    });

                    // Send service message to winner
                    var starGiftTl = new TStarGift
                    {
                        Id = giftDoc.GiftId,
                        Stars = bid.Amount,
                        ConvertStars = giftDoc.ConvertStars,
                        Title = giftDoc.Title,
                        Auction = true,
                        AuctionSlug = auctionToProcess.Slug,
                        GiftsPerRound = auctionToProcess.GiftsPerRound,
                        AuctionStartDate = auctionToProcess.StartDate,
                        Sticker = new TDocument
                        {
                            Id = giftDoc.DocumentId,
                            AccessHash = giftDoc.DocumentAccessHash,
                            FileReference = giftDoc.FileReference,
                            Date = giftDoc.DocumentDate,
                            MimeType = giftDoc.MimeType,
                            Size = giftDoc.DocumentSize,
                            DcId = giftDoc.DcId,
                            Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
                        },
                    };
                    var action = new TMessageActionStarGift
                    {
                        Gift = starGiftTl,
                        AuctionAcquired = true,
                        Saved = true,
                        NameHidden = bid.NameHidden,
                        CanUpgrade = giftDoc.UpgradeStars.HasValue,
                        GiftNum = giftNum,
                    };
                    try
                    {
                        var requestIdBytes = new byte[16];
                        Random.Shared.NextBytes(requestIdBytes);
                        var requestId = new Guid(requestIdBytes);
                        await messageAppService.SendMessageAsync([new SendMessageInput(
                            RequestInfo.Empty with
                            {
                                UserId = MyTelegramConsts.NotificationServiceUserId,
                                Layer = MyTelegramConsts.Layer,
                                Date = now,
                                RequestId = requestId
                            },
                            MyTelegramConsts.NotificationServiceUserId,
                            new Peer(PeerType.User, bid.UserId),
                            string.Empty,
                            randomId,
                            sendMessageType: SendMessageType.MessageService,
                            messageType: MessageType.Text,
                            messageAction: action
                        )]);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to send auctionToProcess winner notification to user {UserId} (this is non-fatal, continuing)", bid.UserId);
                    }
                }
            }

            // Mark winners' bids as consumed (returned=true so they don't carry over)
            foreach (var bid in auctionToProcess.Bids.Where(b => !b.Returned && winnerIds.Contains(b.UserId)))
                bid.Returned = true;

            auctionToProcess.GiftsLeft -= winnerIds.Count;
            auctionToProcess.CurrentRound++;
            auctionToProcess.Version++;

            // Sync AvailabilityRemains in star-gifts collection
            await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
                .UpdateOneAsync(g => g.GiftId == auctionToProcess.GiftId,
                    Builders<StarGiftDocument>.Update.Set(g => g.AvailabilityRemains, auctionToProcess.GiftsLeft));

            bool finished = auctionToProcess.GiftsLeft <= 0 || auctionToProcess.CurrentRound >= auctionToProcess.TotalRounds;
            if (finished)
            {
                auctionToProcess.Finished = true;
                auctionToProcess.NextRoundAt = 0;
                if (winnerIds.Count > 0)
                    auctionToProcess.AveragePrice = totalBid / winnerIds.Count;

                // Refund everyone who never won — deduplicate by userId (keep highest bid per user)
                var refundBids = auctionToProcess.Bids
                    .Where(b => !b.Returned)
                    .GroupBy(b => b.UserId)
                    .Select(g => g.OrderByDescending(b => b.Amount).First())
                    .ToList();
                foreach (var bid in auctionToProcess.Bids.Where(b => !b.Returned))
                    bid.Returned = true;
                
                try
                {
                    foreach (var bid in refundBids)
                    {
                        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, bid.UserId, bid.Amount);
                        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, bid.UserId, bid.Amount,
                            title: "Auction bid refund");
                        try
                        {
                            await SendSystemMessageAsync(bid.UserId, $"Your bid on gift 🎁 was outbid by other auction participants. {bid.Amount} ⭐️ returned to your balance. You can place a new bid.");
                        }
                        catch { /* ignore */ }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error processing refunds for auctionToProcess {GiftId}", auctionToProcess.GiftId);
                }
            }
            else
            {
                // Non-winners carry their bids to next round — no refund yet
                auctionToProcess.NextRoundAt = now + (auctionToProcess.RoundDurationSeconds > 0 ? auctionToProcess.RoundDurationSeconds : 300);
            }

            await auctionToProcessCol.ReplaceOneAsync(a => a.Id == auctionToProcess.Id, auctionToProcess);

            logger.LogInformation("Auction round completed: giftId={GiftId} round={Round} winners={Winners} giftsLeft={Left} finished={Finished}",
                auctionToProcess.GiftId, auctionToProcess.CurrentRound - 1, winnerIds.Count, auctionToProcess.GiftsLeft, auctionToProcess.Finished);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error processing auction {GiftId}, continuing to next", auctionItem.GiftId);
            }
        }
    }

    private async Task SendSystemMessageAsync(long userId, string text)
    {
        try
        {
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with
                {
                    UserId = MyTelegramConsts.NotificationServiceUserId,
                    Layer = MyTelegramConsts.Layer,
                    Date = DateTime.UtcNow.ToTimestamp(),
                    RequestId = Guid.NewGuid()
                },
                MyTelegramConsts.NotificationServiceUserId,
                new Peer(PeerType.User, userId),
                text,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.Text,
                messageType: MessageType.Text
            )]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send system message to userId={UserId}", userId);
        }
    }
}
