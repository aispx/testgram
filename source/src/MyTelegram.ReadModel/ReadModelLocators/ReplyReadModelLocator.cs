using System.Text.Json.Serialization;
using EventFlow.Core;
using EventFlow.ValueObjects;

namespace MyTelegram.ReadModel.ReadModelLocators;

[JsonConverter(typeof(SingleValueObject<ReplyId>))]
public class ReplyId(string value) : Identity<ReplyId>(value)
{
    public static ReplyId Create(long channelId, long messageId)
    {
        return NewDeterministic(GuidFactories.Deterministic.Namespaces.Commands, $"reply-{channelId}-{messageId}");
    }
}

public class ReplyReadModelLocator : IReplyReadModelLocator, ITransientDependency
{
    public IEnumerable<string> GetReadModelIds(IDomainEvent domainEvent)
    {
        var aggregateEvent = domainEvent.GetAggregateEvent();

        switch (aggregateEvent)
        {
            case MessageReplyUpdatedEvent messageReplyUpdatedEvent:
                yield return ReplyId.Create(messageReplyUpdatedEvent.OwnerChannelId, messageReplyUpdatedEvent.MessageId)
                    .Value;
                break;

            case ReplyBroadcastChannelCompletedSagaEvent replyBroadcastChannelCompletedSagaEvent:
                yield return ReplyId.Create(replyBroadcastChannelCompletedSagaEvent.ChannelId,
                    replyBroadcastChannelCompletedSagaEvent.MessageId).Value;
                break;

            case ReplyChannelMessageCompletedEvent replyChannelMessageCompletedEvent:
                yield return ReplyId.Create(replyChannelMessageCompletedEvent.ChannelId, replyChannelMessageCompletedEvent.ReplyToMessageId).Value;
                break;
            // Deleting a comment lowers the counter of the thread root and, when that root is the
            // auto-forwarded channel post, of the post itself — the two rows messages.getMessagesViews
            // reads. See https://corefork.telegram.org/api/threads
            case MessageReplyCountDecrementedEvent messageReplyCountDecrementedEvent:
                yield return ReplyId.Create(messageReplyCountDecrementedEvent.OwnerChannelId,
                    messageReplyCountDecrementedEvent.MessageId).Value;
                if (messageReplyCountDecrementedEvent is { PostChannelId: not null, PostMessageId: not null })
                {
                    yield return ReplyId.Create(messageReplyCountDecrementedEvent.PostChannelId.Value,
                        messageReplyCountDecrementedEvent.PostMessageId.Value).Value;
                }

                break;

            case MessageReplyCreatedSagaEvent messageReplyCreatedSagaEvent:

                yield return ReplyId
                    .Create(messageReplyCreatedSagaEvent.ChannelId, messageReplyCreatedSagaEvent.MessageId).Value;
                break;

        }
    }
}
