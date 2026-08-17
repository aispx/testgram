namespace MyTelegram.ReadModel.ReadModelLocators;

public class ChatInviteImporterReadModelLocator : IChatInviteImporterReadModelLocator, ITransientDependency
{
    public IEnumerable<string> GetReadModelIds(IDomainEvent domainEvent)
    {
        var aggregateEvent = domainEvent.GetAggregateEvent();
        switch (aggregateEvent)
        {
            case ChatInviteImportedEvent chatInviteImportedEvent:
                yield return ChatInviteImporterId
                    .Create(chatInviteImportedEvent.ChannelId, chatInviteImportedEvent.RequestInfo.UserId).Value;
                break;
            case ChatInviteCreatedEvent chatInviteCreatedEvent:
                yield return ChatInviteImporterId
                    .Create(chatInviteCreatedEvent.ChannelId, chatInviteCreatedEvent.RequestInfo.UserId).Value;
                break;

            case JoinChannelRequestUpdatedEvent joinChannelRequestUpdatedEvent:
                yield return ChatInviteImporterId
                    .Create(joinChannelRequestUpdatedEvent.ChannelId, joinChannelRequestUpdatedEvent.UserId).Value;
                break;
        }
    }
}