using MongoDB.Driver;
using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Generate an <a href="https://corefork.telegram.org/api/links#invoice-links">invoice deep link</a>
/// Possible errors
/// Code Type Description
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 CURRENCY_TOTAL_AMOUNT_INVALID The total amount of all prices is invalid.
/// 400 INVOICE_PAYLOAD_INVALID The specified invoice payload is invalid.
/// 400 MEDIA_INVALID Media invalid.
/// 400 PAYMENT_PROVIDER_INVALID The specified payment provider is invalid.
/// 400 STARS_INVOICE_INVALID The specified Telegram Star invoice is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 WEBDOCUMENT_MIME_INVALID Invalid webdocument mime type provided.
/// 400 WEBDOCUMENT_URL_EMPTY The passed web document URL is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.exportInvoice"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ExportInvoiceHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestExportInvoice, MyTelegram.Schema.Payments.IExportedInvoice>
{
    protected override async Task<MyTelegram.Schema.Payments.IExportedInvoice> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestExportInvoice obj)
    {
        var bot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (bot == null || !bot.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.InvoiceMedia is not TInputMediaInvoice invoiceMedia)
        {
            RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
            return null!;
        }

        if (invoiceMedia.Invoice is not { } details || details.Prices is not { Count: > 0 })
        {
            RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
            return null!;
        }

        // Only Telegram Stars invoices can be settled here: there is no per bot payment provider, so a
        // fiat link would lead to a form nothing can charge.
        if (details.Currency != BotInvoiceHelper.StarsCurrency ||
            BotInvoiceHelper.GetTotalAmount(details) <= 0)
        {
            RpcErrors.RpcErrors400.StarsInvoiceInvalid.ThrowRpcError();
        }

        if (invoiceMedia.Payload.Length == 0)
        {
            RpcErrors.RpcErrors400.InvoicePayloadInvalid.ThrowRpcError();
        }

        if (invoiceMedia.Photo is TInputWebDocument photo)
        {
            if (string.IsNullOrWhiteSpace(photo.Url))
            {
                RpcErrors.RpcErrors400.WebDocumentUrlEmpty.ThrowRpcError();
            }

            if (string.IsNullOrWhiteSpace(photo.MimeType))
            {
                RpcErrors.RpcErrors400.WebDocumentMimeInvalid.ThrowRpcError();
            }
        }

        // A link only invoice has no message to hang off, so it is addressed purely by its slug.
        var storedInvoice = BotInvoiceHelper.Create(
            invoiceMedia,
            botId: input.UserId,
            ownerPeerId: 0,
            toPeerId: 0,
            msgId: 0);

        await BotInvoiceHelper.SaveAsync(mongoDatabase, storedInvoice);

        return new MyTelegram.Schema.Payments.TExportedInvoice
        {
            Url = $"https://t.me/${storedInvoice.Slug}"
        };
    }
}