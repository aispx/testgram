namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Clear all <a href="https://corefork.telegram.org/api/drafts">drafts</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.clearAllDrafts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Each draft is dropped through the dialog it belongs to rather than deleted from the draft read
/// model alone: that is what also clears <c>dialog.draft</c> (otherwise the next
/// <c>messages.getDialogs</c> hands every draft straight back) and what pushes
/// <c>updateDraftMessage</c> with <c>draftMessageEmpty</c> to the other sessions. TDLib clears nothing
/// but secret chats locally and waits for those updates.</para>
/// </remarks>
internal sealed class ClearAllDraftsHandler(IQueryProcessor queryProcessor, ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestClearAllDrafts, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestClearAllDrafts obj)
    {
        var drafts = await queryProcessor.ProcessAsync(new GetAllDraftQuery(input.UserId));
        var requestInfo = input.ToRequestInfo();

        // Grouped by dialog, one command each: a request command is deduplicated by the request's
        // msg_id alone, so a second command for the same dialog would be skipped and that draft would
        // survive.
        var byDialog = drafts
            .Where(p => p.Peer != null)
            .GroupBy(p => DialogId.Create(input.UserId, p.Peer).Value);

        foreach (var dialogDrafts in byDialog)
        {
            var peer = dialogDrafts.First().Peer;
            var topics = dialogDrafts
                .Select(p => new DraftTopic(p.Draft?.TopMsgId, p.Draft?.SavedPeerId))
                .Distinct()
                .ToList();

            var command = new ClearDraftsCommand(DialogId.Create(input.UserId, peer),
                requestInfo,
                input.UserId,
                peer,
                topics);
            await commandBus.PublishAsync(command);
        }

        return new TBoolTrue();
    }
}
