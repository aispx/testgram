using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Periodically scans <c>suggested_post_settlements</c> and emits
/// <see cref="TMessageActionSuggestedPostSuccess"/> service messages for any pending
/// settlements whose <c>SettleAt</c> has elapsed.
/// </summary>
public class SuggestedPostSettlementBackgroundService(
    IMongoDatabase mongoDatabase,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<SuggestedPostSettlementBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Suggested post settlement worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Suggested post settlement worker error");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessOnceAsync(CancellationToken ct)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var col = mongoDatabase.GetCollection<SuggestedPostSettlementDocument>("suggested_post_settlements");
        var filter = Builders<SuggestedPostSettlementDocument>.Filter.Eq(x => x.Status, "pending")
                     & Builders<SuggestedPostSettlementDocument>.Filter.Lte(x => x.SettleAt, now);

        var due = await col.Find(filter).Limit(100).ToListAsync(ct);
        if (due.Count == 0) return;

        using var scope = serviceScopeFactory.CreateScope();
        var messageAppService = scope.ServiceProvider.GetRequiredService<IMessageAppService>();

        foreach (var doc in due)
        {
            try
            {
                IStarsAmount price = doc.Currency == "ton"
                    ? new TStarsTonAmount { Amount = doc.Amount }
                    : new TStarsAmount { Amount = doc.Amount };

                var action = new TMessageActionSuggestedPostSuccess { Price = price };

                var sendInput = new SendMessageInput(
                    new RequestInfo(
                        ConnectionId: string.Empty,
                        SessionId: 0,
                        ReqMsgId: 0,
                        UserId: doc.ApproverUserId,
                        AccessHashKeyId: 0,
                        AuthKeyId: 0,
                        PermAuthKeyId: 0,
                        RequestId: Guid.NewGuid(),
                        Layer: 222,
                        Date: DateTime.UtcNow.ToTimestamp(),
                        DeviceType: DeviceType.Android
                    ),
                    senderUserId: doc.ApproverUserId,
                    toPeer: new Peer(PeerType.Channel, doc.MonoforumChannelId),
                    message: string.Empty,
                    randomId: Random.Shared.NextInt64(),
                    inputReplyTo: new TInputReplyToMessage { ReplyToMsgId = doc.OriginalMsgId },
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: action,
                    savedPeerId: new Peer(PeerType.User, doc.AuthorUserId)
                );

                await messageAppService.SendMessageAsync(new List<SendMessageInput> { sendInput });

                await col.UpdateOneAsync(
                    Builders<SuggestedPostSettlementDocument>.Filter.Eq(x => x.Id, doc.Id),
                    Builders<SuggestedPostSettlementDocument>.Update.Set(x => x.Status, "success"),
                    cancellationToken: ct);

                logger.LogInformation("Settled suggested post {Id} for {Amount} {Currency}", doc.Id, doc.Amount, doc.Currency);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to settle suggested post {Id}", doc.Id);
            }
        }
    }
}
