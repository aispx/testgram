using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Submit requested order information for validation
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.validateRequestedInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ValidateRequestedInfoHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IObjectMessageSender objectMessageSender,
    ILogger<ValidateRequestedInfoHandler> logger)
    : RpcResultObjectHandler<RequestValidateRequestedInfo, IValidatedRequestedInfo>
{
    protected override async Task<IValidatedRequestedInfo> HandleCoreAsync(IRequestInput input, RequestValidateRequestedInfo obj)
    {
        if (obj.Info == null)
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        var (botId, payload, shippingAddressRequested, flexible) = await ResolveInvoiceAsync(input, obj.Invoice);

        TVector<IShippingOption>? shippingOptions = null;
        if (shippingAddressRequested && flexible)
        {
            if (obj.Info.ShippingAddress == null)
            {
                RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
            }

            var shippingResult = await SendShippingQueryAndWaitAsync(botId, input.UserId, payload, obj.Info.ShippingAddress);
            if (!shippingResult.Success)
            {
                throw new RpcException(new RpcError(400, shippingResult.Error ?? RpcErrors.RpcErrors400.BotResponseTimeout.Message));
            }

            shippingOptions = shippingResult.ShippingOptions;
        }

        var validationId = Guid.NewGuid().ToString("N");
        var infoBytes = obj.Info.ToBytes();

        await mongoDatabase.GetCollection<BsonDocument>("payment-requested-info-validations")
            .ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", validationId),
                new BsonDocument
                {
                    ["_id"] = validationId,
                    ["user_id"] = input.UserId,
                    ["bot_id"] = botId,
                    ["invoice_type"] = obj.Invoice.GetType().Name,
                    ["info"] = infoBytes,
                    ["shipping_options"] = shippingOptions?.ToBytes() ?? Array.Empty<byte>(),
                    ["created_at"] = DateTime.UtcNow.ToTimestamp()
                },
                new ReplaceOptions { IsUpsert = true });

        if (obj.Save)
        {
            var savedInfoCol = mongoDatabase.GetCollection<SavedPaymentRequestedInfoDocument>("saved-payment-requested-info");
            await savedInfoCol.ReplaceOneAsync(
                x => x.UserId == input.UserId,
                new SavedPaymentRequestedInfoDocument
                {
                    UserId = input.UserId,
                    Info = infoBytes,
                    UpdatedAt = DateTime.UtcNow,
                },
                new ReplaceOptions { IsUpsert = true });
        }

        return new TValidatedRequestedInfo
        {
            Id = validationId,
            ShippingOptions = shippingOptions,
        };
    }

    /// <summary>
    /// Reads the invoice the client is validating against out of the server side store.
    /// </summary>
    /// <remarks>
    /// <c>flexible</c> and the bot API <c>payload</c> live only here — <c>messageMediaInvoice</c> never
    /// carried them — and without both the shipping query below can neither fire nor be matched to an
    /// order by the bot.
    /// </remarks>
    private async Task<(long BotId, byte[] Payload, bool ShippingAddressRequested, bool Flexible)> ResolveInvoiceAsync(
        IRequestInput input,
        IInputInvoice invoice)
    {
        if (invoice is not (TInputInvoiceMessage or TInputInvoiceSlug))
        {
            // Stars top-ups, gifts and giveaways carry no bot invoice: nothing to ask a bot about.
            return (MyTelegramConsts.NotificationServiceUserId, [], false, false);
        }

        if (invoice is TInputInvoiceMessage { Peer: null })
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var storedInvoice = await BotInvoiceHelper.ResolveAsync(mongoDatabase, peerHelper, input, invoice);
        if (storedInvoice == null)
        {
            // Paid media invoices also arrive as inputInvoiceMessage but have no bot invoice behind
            // them, so there is nobody to send a shipping query to; the info is simply stored.
            return (MyTelegramConsts.NotificationServiceUserId, [], false, false);
        }

        var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(storedInvoice.BotId));
        if (botUser == null || !botUser.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var details = BotInvoiceHelper.ReadInvoice(storedInvoice);
        return (storedInvoice.BotId, storedInvoice.Payload, details.ShippingAddressRequested, details.Flexible);
    }

    private async Task<(bool Success, string? Error, TVector<IShippingOption>? ShippingOptions)> SendShippingQueryAndWaitAsync(
        long botId,
        long userId,
        byte[] payload,
        IPostAddress shippingAddress)
    {
        var queryId = Random.Shared.NextInt64();
        var collection = mongoDatabase.GetCollection<BsonDocument>("pending_shipping_queries");
        await collection.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"shipping-{queryId}",
            ["query_id"] = queryId,
            ["bot_id"] = botId,
            ["user_id"] = userId,
            ["payload"] = payload,
            ["shipping_address"] = shippingAddress.ToBytes(),
            ["shipping_options"] = Array.Empty<byte>(),
            ["created_at"] = DateTime.UtcNow.ToTimestamp(),
            ["success"] = false,
            ["error"] = string.Empty,
            ["responded_at"] = 0
        });

        var update = new TUpdateBotShippingQuery
        {
            QueryId = queryId,
            UserId = userId,
            Payload = payload,
            ShippingAddress = shippingAddress,
        };

        try
        {
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, botId),
                new TUpdates
                {
                    Updates = [update],
                    Users = [],
                    Chats = [],
                    Date = DateTime.UtcNow.ToTimestamp(),
                    Seq = 0
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send shipping query to bot: queryId={QueryId} botId={BotId}", queryId, botId);
            await collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("query_id", queryId));
            return (false, "BOT_RESPONSE_TIMEOUT", null);
        }

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            await Task.Delay(500);

            var filter = Builders<BsonDocument>.Filter.Eq("query_id", queryId);
            var query = await collection.Find(filter).FirstOrDefaultAsync();
            if (query != null && GetInt32(query, "responded_at") > 0)
            {
                await collection.DeleteOneAsync(filter);

                var success = GetBool(query, "success");
                var error = query.TryGetValue("error", out var errorValue) && errorValue.IsString ? errorValue.AsString : null;
                return (success, error, ReadShippingOptions(query));
            }
        }

        await collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("query_id", queryId));
        return (false, "BOT_RESPONSE_TIMEOUT", null);
    }

    private static TVector<IShippingOption>? ReadShippingOptions(BsonDocument document)
    {
        var bytes = GetBytes(document, "shipping_options");
        if (bytes.Length == 0)
        {
            return null;
        }

        var buffer = new ReadOnlyMemory<byte>(bytes);
        var shippingOptions = buffer.Read<TVector<IShippingOption>>();
        return shippingOptions.Count > 0 ? shippingOptions : null;
    }

    private static long GetInt64(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }

    private static int GetInt32(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => (int)value.AsInt64,
            BsonType.Double => (int)value.AsDouble,
            _ => 0
        };
    }

    private static bool GetBool(BsonDocument document, string name)
    {
        return document.TryGetValue(name, out var value) &&
               value.BsonType == BsonType.Boolean &&
               value.AsBoolean;
    }

    private static byte[] GetBytes(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value) || value.IsBsonNull)
        {
            return [];
        }

        return value.BsonType switch
        {
            BsonType.Binary => value.AsBsonBinaryData.Bytes,
            BsonType.String => Convert.FromBase64String(value.AsString),
            _ => []
        };
    }
}
