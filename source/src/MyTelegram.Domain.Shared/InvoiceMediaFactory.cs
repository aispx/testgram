// ReSharper disable once CheckNamespace

namespace MyTelegram;

/// <summary>
/// Attaches the receipt message to an invoice's media.
/// </summary>
/// <remarks>
/// <para>
/// Once an invoice has been paid, <c>messageMediaInvoice.receipt_msg_id</c> points at the service
/// message the checkout generated. Clients read it as <c>messageInvoice.receipt_message_id</c>
/// (tdlib <c>InputInvoice.cpp</c>) and use it to turn the invoice bubble's <em>Pay</em> button into
/// <em>Receipt</em>, so without it a paid invoice keeps offering to be paid again.
/// </para>
/// <para>
/// Shared by the message aggregate state, the read model and the payment service, so the mapping
/// lives in exactly one place — same arrangement as <see cref="TodoMediaFactory"/>.
/// </para>
/// <para>See https://corefork.telegram.org/api/payments#5-checkout </para>
/// </remarks>
public static class InvoiceMediaFactory
{
    /// <summary>
    /// Returns <paramref name="media"/> with <c>receipt_msg_id</c> set, or the media unchanged when it
    /// is not an invoice or the receipt is already recorded.
    /// </summary>
    public static IMessageMedia? WithReceipt(IMessageMedia? media, int receiptMsgId)
    {
        if (receiptMsgId <= 0 || media is not TMessageMediaInvoice invoice || invoice.ReceiptMsgId == receiptMsgId)
        {
            return media;
        }

        // The stored media object is shared with the aggregate state, so it is copied rather than
        // mutated in place.
        return new TMessageMediaInvoice
        {
            ShippingAddressRequested = invoice.ShippingAddressRequested,
            Test = invoice.Test,
            Title = invoice.Title,
            Description = invoice.Description,
            Photo = invoice.Photo,
            ReceiptMsgId = receiptMsgId,
            Currency = invoice.Currency,
            TotalAmount = invoice.TotalAmount,
            StartParam = invoice.StartParam,
            ExtendedMedia = invoice.ExtendedMedia
        };
    }
}
