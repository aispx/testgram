using EventFlow.Aggregates;
using EventFlow.Subscribers;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Sagas.Events;

namespace MyTelegram.Messenger.CommandServer.EventHandlers;

public class CreateFragmentCollectibleForPhoneEventHandler : ISubscribeSynchronousTo<UserSignUpSaga, UserSignUpSagaId, UserSignUpSuccessSagaEvent>
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly ILogger<CreateFragmentCollectibleForPhoneEventHandler> _logger;

    public CreateFragmentCollectibleForPhoneEventHandler(
        IMongoDatabase mongoDatabase,
        ILogger<CreateFragmentCollectibleForPhoneEventHandler> logger)
    {
        _mongoDatabase = mongoDatabase;
        _logger = logger;
    }

    public Task HandleAsync(IDomainEvent<UserSignUpSaga, UserSignUpSagaId, UserSignUpSuccessSagaEvent> domainEvent, CancellationToken cancellationToken)
    {
        var phoneNumber = domainEvent.AggregateEvent.PhoneNumber;
        var userId = domainEvent.AggregateEvent.UserId;

        // Create Fragment collectible for +888 numbers
        if (phoneNumber.StartsWith("888"))
        {
            var collection = _mongoDatabase.GetCollection<BsonDocument>("fragment_collectibles");
            var doc = new BsonDocument
            {
                ["_id"] = $"fragment-phone-{phoneNumber}",
                ["type"] = "phone",
                ["phone"] = phoneNumber,
                ["purchase_date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["currency"] = "USD",
                ["amount"] = 29900, // $299.00
                ["crypto_currency"] = "TON",
                ["crypto_amount"] = 100000000000L, // 100 TON
                ["url"] = $"https://fragment.com/number/{phoneNumber}"
            };

            try
            {
                collection.InsertOne(doc, cancellationToken: cancellationToken);
                _logger.LogInformation("Created Fragment collectible for phone {PhoneNumber} (UserId: {UserId})", phoneNumber, userId);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Already exists, ignore
                _logger.LogDebug("Fragment collectible for phone {PhoneNumber} already exists", phoneNumber);
            }
        }

        return Task.CompletedTask;
    }
}
