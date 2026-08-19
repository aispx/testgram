using MyTelegram.Messenger.Handlers.LatestLayer.Messages;

namespace MyTelegram.Messenger.Services.Scheduled;

/// <summary>
/// Builds the <c>messages.Messages</c> answer of messages.getScheduledHistory / getScheduledMessages.
/// </summary>
public interface IScheduledMessagesResponseBuilder
{
    Task<MyTelegram.Schema.Messages.IMessages> ToMessagesAsync(IRequestInput input, Peer peer,
        IReadOnlyList<ScheduledMessageDocument> documents, long hash = 0);
}

/// <inheritdoc />
public class ScheduledMessagesResponseBuilder(
    IScheduledMessageStore store,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService)
    : IScheduledMessagesResponseBuilder, ITransientDependency
{
    public async Task<MyTelegram.Schema.Messages.IMessages> ToMessagesAsync(IRequestInput input, Peer peer,
        IReadOnlyList<ScheduledMessageDocument> documents, long hash = 0)
    {
        // "To generate the hash, populate the ids array with the id, edit_date (0 if unedited) and date
        // (in this order) of the previously returned messages".
        // See https://corefork.telegram.org/api/offsets#hash-generation
        if (hash != 0 && CalcHash(documents) == hash)
        {
            return new TMessagesNotModified { Count = documents.Count };
        }

        var messages = new TVector<IMessage>(documents.Select(p => store.Render(p, input.UserId, input.Layer)));

        var userIds = documents.Select(p => p.SenderUserId).ToList();
        if (peer.PeerType == PeerType.User)
        {
            userIds.Add(peer.PeerId);
        }

        var users = await userConverterService.GetUserListAsync(input, userIds.Distinct().ToList(),
            layer: input.Layer);

        var chats = peer.PeerType == PeerType.Channel
            ? await chatConverterService.GetChannelListAsync(input, [peer.PeerId], layer: input.Layer)
            : [];

        return new TMessages
        {
            Messages = messages,
            Chats = [.. chats],
            Users = [.. users],
            Topics = new TVector<IForumTopic>()
        };
    }

    public static long CalcHash(IReadOnlyList<ScheduledMessageDocument> documents)
    {
        var hash = 0L;
        foreach (var document in documents)
        {
            hash = MessageSearchMongoHelper.CalcHash(hash, document.ScheduledMessageId);
            hash = MessageSearchMongoHelper.CalcHash(hash, document.EditDate ?? 0);
            hash = MessageSearchMongoHelper.CalcHash(hash, document.ScheduleDate);
        }

        return hash;
    }
}
