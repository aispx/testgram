using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Get all contacts, requires a <a href="https://corefork.telegram.org/api/takeout">takeout session, see here » for more info</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.getSaved"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedHandler(IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestGetSaved, TVector<MyTelegram.Schema.ISavedContact>>
{
    protected override async Task<TVector<MyTelegram.Schema.ISavedContact>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestGetSaved obj)
    {
        if (TakeoutContext.CurrentSession is not { Contacts: true })
        {
            RpcErrors.RpcErrors403.TakeoutRequired.ThrowRpcError();
        }

        var contacts = await queryProcessor.ProcessAsync(new GetImportedContactsByUserIdQuery(input.UserId), CancellationToken.None);
        return [.. contacts.Select(p => new TSavedPhoneContact
        {
            Phone = p.Phone,
            FirstName = p.FirstName,
            LastName = p.LastName ?? string.Empty,
            Date = 0,
        })];
    }
}
