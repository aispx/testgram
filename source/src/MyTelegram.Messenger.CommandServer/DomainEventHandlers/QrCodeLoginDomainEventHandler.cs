using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.CommandServer.DomainEventHandlers;

public class QrCodeLoginDomainEventHandler(
    ICacheHelper<long, CacheLoginToken> authKeyCacheHelper,
    ICacheHelper<string, CacheLoginToken> tokenCacheHelper) : ISubscribeSynchronousTo<QrCodeAggregate, QrCodeId, LoginTokenAcceptedEvent>
{
    public Task HandleAsync(IDomainEvent<QrCodeAggregate, QrCodeId, LoginTokenAcceptedEvent> domainEvent, CancellationToken cancellationToken)
    {
        var loginToken = new CacheLoginToken(
            domainEvent.AggregateEvent.QrCodeLoginRequestTempAuthKeyId,
            domainEvent.AggregateEvent.UserId,
            domainEvent.AggregateEvent.Token);
        authKeyCacheHelper.TryAdd(
            domainEvent.AggregateEvent.QrCodeLoginRequestTempAuthKeyId,
            loginToken);
        tokenCacheHelper.TryAdd(CacheLoginToken.GetTokenKey(domainEvent.AggregateEvent.Token), loginToken);

        return Task.CompletedTask;
    }
}
