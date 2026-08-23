using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get info about a credit card
/// Possible errors
/// Code Type Description
/// 400 BANK_CARD_NUMBER_INVALID The specified card number is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getBankCardData"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBankCardDataHandler(IBankCardBinProvider bankCardBinProvider)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetBankCardData, MyTelegram.Schema.Payments.IBankCardData>
{
    protected override Task<MyTelegram.Schema.Payments.IBankCardData> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestGetBankCardData obj)
    {
        var entry = bankCardBinProvider.Resolve(obj.Number);
        if (entry == null)
        {
            RpcErrors.RpcErrors400.BankCardNumberInvalid.ThrowRpcError();
        }

        var openUrls = new TVector<IBankCardOpenUrl>();
        if (!string.IsNullOrEmpty(entry!.Url))
        {
            openUrls.Add(new TBankCardOpenUrl { Name = entry.Title, Url = entry.Url });
        }

        return Task.FromResult<MyTelegram.Schema.Payments.IBankCardData>(
            new MyTelegram.Schema.Payments.TBankCardData
            {
                Title = entry.Title,
                OpenUrls = openUrls
            });
    }
}