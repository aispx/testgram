using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Payments;

/// <summary>
/// Feature: linking a paid invoice to its receipt.
///
/// <para>
/// After checkout the invoice's <c>messageMediaInvoice.receipt_msg_id</c> points at the payment
/// service message. Clients read it as <c>messageInvoice.receipt_message_id</c> (tdlib
/// <c>InputInvoice.cpp</c>) and swap the bubble's <em>Pay</em> button for <em>Receipt</em>, so an
/// invoice that never gets it keeps offering to be paid a second time.
/// See https://corefork.telegram.org/api/payments#5-checkout
/// </para>
/// </summary>
public class InvoiceMediaFactoryTests
{
    [Fact]
    public void The_receipt_is_recorded_on_the_invoice()
    {
        var updated = InvoiceMediaFactory.WithReceipt(CreateInvoice(), 4242);

        updated.ShouldBeOfType<TMessageMediaInvoice>().ReceiptMsgId.ShouldBe(4242);
    }

    [Fact]
    public void Every_other_invoice_field_survives()
    {
        var original = CreateInvoice();

        var updated = InvoiceMediaFactory.WithReceipt(original, 7).ShouldBeOfType<TMessageMediaInvoice>();

        updated.Title.ShouldBe(original.Title);
        updated.Description.ShouldBe(original.Description);
        updated.Currency.ShouldBe(original.Currency);
        updated.TotalAmount.ShouldBe(original.TotalAmount);
        updated.StartParam.ShouldBe(original.StartParam);
        updated.ShippingAddressRequested.ShouldBe(original.ShippingAddressRequested);
        updated.Test.ShouldBe(original.Test);
        updated.Photo.ShouldBe(original.Photo);
    }

    [Fact]
    public void The_stored_media_is_copied_rather_than_mutated()
    {
        // The aggregate state hands its own media object in; mutating it would rewrite history.
        var original = CreateInvoice();

        InvoiceMediaFactory.WithReceipt(original, 99);

        original.ReceiptMsgId.ShouldBeNull();
    }

    [Fact]
    public void Media_that_is_not_an_invoice_is_left_alone()
    {
        var media = new TMessageMediaEmpty();

        InvoiceMediaFactory.WithReceipt(media, 5).ShouldBeSameAs(media);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_meaningless_receipt_id_is_ignored(int receiptMsgId)
    {
        var media = CreateInvoice();

        InvoiceMediaFactory.WithReceipt(media, receiptMsgId).ShouldBeSameAs(media);
    }

    [Fact]
    public void Recording_the_same_receipt_twice_changes_nothing()
    {
        // The publisher writes to both copies of the message; a redelivered command must be a no-op
        // so it cannot produce a pointless updateEditMessage.
        var media = CreateInvoice();
        media.ReceiptMsgId = 11;

        InvoiceMediaFactory.WithReceipt(media, 11).ShouldBeSameAs(media);
    }

    [Fact]
    public void Nothing_is_invented_for_missing_media()
    {
        InvoiceMediaFactory.WithReceipt(null, 3).ShouldBeNull();
    }

    private static TMessageMediaInvoice CreateInvoice()
    {
        return new TMessageMediaInvoice
        {
            Title = "Rubber duck",
            Description = "A yellow one",
            Currency = "XTR",
            TotalAmount = 50,
            StartParam = "duck",
            ShippingAddressRequested = true,
            Test = true,
            Photo = new TWebDocumentNoProxy
            {
                Url = "https://example.test/duck.jpg",
                Size = 1024,
                MimeType = "image/jpeg",
                Attributes = new TVector<IDocumentAttribute>()
            }
        };
    }
}
