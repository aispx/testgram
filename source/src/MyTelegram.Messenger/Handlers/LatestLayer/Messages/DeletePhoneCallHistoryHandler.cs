namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Delete the entire phone call history.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deletePhoneCallHistory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeletePhoneCallHistoryHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPtsHelper ptsHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeletePhoneCallHistory, MyTelegram.Schema.Messages.IAffectedFoundMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedFoundMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestDeletePhoneCallHistory obj)
    {
        var messageItemsToBeDeleted = await queryProcessor.ProcessAsync(new GetPhoneCallHistoryToBeDeletedQuery(
            input.UserId,
            MyTelegramConsts.ClearHistoryDefaultPageSize,
            obj.Revoke));
        if (messageItemsToBeDeleted.Count == 0)
        {
            return new TAffectedFoundMessages
            {
                Offset = 0,
                Pts = ptsHelper.GetCachedPts(input.UserId),
                PtsCount = 0,
                Messages = new TVector<int>()
            };
        }

        var command = new StartDeleteHistoryCommand(
            TempId.New,
            input.ToRequestInfo(),
            messageItemsToBeDeleted,
            obj.Revoke,
            obj.Revoke,
            true);
        await commandBus.PublishAsync(command);
        return null!;
    }
}
