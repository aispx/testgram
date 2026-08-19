using EventFlow.Exceptions;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Notifies the sender about the recipient having listened a voice message or watched a video, emitting an <a href="https://corefork.telegram.org/constructor/updateReadMessagesContents">updateReadMessagesContents</a>.
/// Also clears the @ badge of the messages, see <a href="https://corefork.telegram.org/api/mentions">mentions</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readMessageContents"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadMessageContentsHandler(
    IQueryProcessor queryProcessor,
    IPtsHelper ptsHelper,
    ICommandBus commandBus,
    IMentionReadStateService mentionReadStateService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadMessageContents, MyTelegram.Schema.Messages.IAffectedMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadMessageContents obj)
    {
        var messageIds = obj.Id?.Distinct().Where(p => p > 0).ToList() ?? [];
        if (messageIds.Count == 0)
        {
            return new TAffectedMessages { Pts = ptsHelper.GetCachedPts(input.UserId), PtsCount = 0 };
        }

        // Scoped to the caller's own box: this method takes bare message ids, so another user's
        // messages must not be reachable with a guessed id.
        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesByOwnerAndMessageIdListQuery(input.UserId, messageIds));

        var mentioned = messages
            .Where(p => p.MentionedUserIds?.Contains(input.UserId) ?? false)
            .ToList();

        foreach (var group in mentioned.GroupBy(p => new Peer(p.ToPeerType, p.ToPeerId)))
        {
            var ids = group.Select(p => p.MessageId).ToList();
            await mentionReadStateService.MarkReadAsync(input.UserId, group.Key, ids);

            foreach (var messageId in ids)
            {
                try
                {
                    await commandBus.PublishAsync(
                        new ReadMentionCommand(DialogId.Create(input.UserId, group.Key), messageId));
                }
                catch (DomainError)
                {
                    // No dialog aggregate (for example a legacy chat): the badge is best-effort.
                }
            }
        }

        // Advance pts so the other sessions of this user notice the read state.
        var currentPts = ptsHelper.GetCachedPts(input.UserId);
        var pts = await ptsHelper.IncrementPtsAsync(input.UserId, currentPts, 1, input.PermAuthKeyId);

        return new TAffectedMessages { Pts = pts, PtsCount = 1 };
    }
}
