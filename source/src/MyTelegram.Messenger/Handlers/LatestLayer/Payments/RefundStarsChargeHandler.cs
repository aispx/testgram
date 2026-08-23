using MongoDB.Driver;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Refund a <a href="https://corefork.telegram.org/api/stars">Telegram Stars</a> transaction, see <a href="https://corefork.telegram.org/api/payments#6-refunds">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 CHARGE_ALREADY_REFUNDED The transaction was already refunded.
/// 400 CHARGE_ID_EMPTY The specified charge_id is empty.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.refundStarsCharge"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class RefundStarsChargeHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IMessageAppService messageAppService,
    ILogger<RefundStarsChargeHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestRefundStarsCharge, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestRefundStarsCharge obj)
    {
        var bot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (bot == null || !bot.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (string.IsNullOrEmpty(obj.ChargeId))
        {
            RpcErrors.RpcErrors400.ChargeIdEmpty.ThrowRpcError();
        }

        var buyerPeer = peerHelper.GetPeer(obj.UserId, input.UserId);
        if (buyerPeer == null || buyerPeer.PeerType != PeerType.User || buyerPeer.PeerId == input.UserId)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var buyerUserId = buyerPeer!.PeerId;
        var transactions = mongoDatabase.GetCollection<StarsTransactionDocument>("star-transactions");

        // Scoped to the calling bot's own credit row, so a bot cannot refund a charge it never
        // received, and to the quoted buyer, so it cannot pay the wrong user back.
        var charge = await transactions
            .Find(x => x.TransactionId == obj.ChargeId
                       && x.UserId == input.UserId
                       && x.PeerUserId == buyerUserId
                       && x.Amount > 0
                       && !x.Refund)
            .FirstOrDefaultAsync();

        if (charge == null)
        {
            RpcErrors.RpcErrors400.ChargeIdInvalid.ThrowRpcError();
        }

        if (charge!.RefundedAt.HasValue)
        {
            RpcErrors.RpcErrors400.ChargeAlreadyRefunded.ThrowRpcError();
        }

        var amount = charge.Amount;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Claim the charge before moving any stars: a second concurrent call finds RefundedAt already
        // set and gets CHARGE_ALREADY_REFUNDED instead of paying the buyer back twice.
        var claimed = await transactions.UpdateOneAsync(
            Builders<StarsTransactionDocument>.Filter.And(
                Builders<StarsTransactionDocument>.Filter.Eq(x => x.Id, charge.Id),
                Builders<StarsTransactionDocument>.Filter.Eq(x => x.RefundedAt, null)),
            Builders<StarsTransactionDocument>.Update.Set(x => x.RefundedAt, now));

        if (claimed.ModifiedCount == 0)
        {
            RpcErrors.RpcErrors400.ChargeAlreadyRefunded.ThrowRpcError();
        }

        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -amount);
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, buyerUserId, amount);

        await StarsBalanceHelper.AddTransactionAsync(
            mongoDatabase, input.UserId, -amount,
            title: charge.Title, peerUserId: buyerUserId, refund: true);

        await StarsBalanceHelper.AddTransactionAsync(
            mongoDatabase, buyerUserId, amount,
            title: charge.Title, peerUserId: input.UserId, refund: true);

        // "This will emit a messageActionPaymentRefunded service message."
        // https://corefork.telegram.org/api/payments#6-refunds
        var action = new TMessageActionPaymentRefunded
        {
            Peer = new TPeerUser { UserId = buyerUserId },
            Currency = BotInvoiceHelper.StarsCurrency,
            TotalAmount = amount,
            Charge = new TPaymentCharge { Id = obj.ChargeId, ProviderChargeId = obj.ChargeId }
        };

        await messageAppService.SendMessageAsync([
            new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                new Peer(PeerType.User, buyerUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action)
        ]);

        logger.LogInformation("Refunded stars charge: chargeId={ChargeId} botId={BotId} userId={UserId} amount={Amount}",
            obj.ChargeId, input.UserId, buyerUserId, amount);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}