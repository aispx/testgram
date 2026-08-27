using MyTelegram.Domain.Aggregates.Temp;

namespace MyTelegram.ReadModel.ReadModelLocators;

public class DraftReadModelLocator : IReadModelLocator, ITransientDependency
{
    public IEnumerable<string> GetReadModelIds(IDomainEvent domainEvent)
    {
        var aggregateEvent = domainEvent.GetAggregateEvent();
        switch (aggregateEvent)
        {
            // One row per (dialog, topic): a draft in a forum or monoforum topic must not overwrite
            // the draft of the chat itself. The chat level draft keeps the bare dialog id.
            case DraftSavedEvent draftSavedEvent:
                yield return DraftTopicKey.ToReadModelId(domainEvent.GetIdentity().Value,
                    DraftTopicKey.Create(draftSavedEvent.Draft));
                break;
            case DraftClearedEvent draftClearedEvent:
                foreach (var topic in DraftTopicKey.OrChatLevel(draftClearedEvent.Topics))
                {
                    yield return DraftTopicKey.ToReadModelId(domainEvent.GetIdentity().Value, topic.Key);
                }

                break;
            case DraftDeletedEvent draftDeletedEvent:
                yield return DialogId.Create(draftDeletedEvent.OwnerPeerId, draftDeletedEvent.ToPeer).Value;
                break;
        }
    }
}
