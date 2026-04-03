# Testgram Development Guide v2.0

Self-hosted C# Telegram server (fork of MyTelegram). MTProto 2.0, API Layer 222.

**Stack:** .NET 10, CQRS + Event Sourcing (EventFlow), MongoDB, RabbitMQ, Redis, MinIO, Coturn

**Architecture:** Event Sourcing + CQRS with EventFlow framework

---

## 🏗️ Architecture Overview

### Event Sourcing + CQRS Pattern

```
┌─────────────┐
│   Client    │
└──────┬──────┘
       │
       ▼
┌─────────────────────────────────────────────────────────┐
│              Gateway Server (MTProto)                    │
└──────┬──────────────────────────────────────────┬───────┘
       │                                           │
       ▼                                           ▼
┌─────────────────┐                      ┌─────────────────┐
│ Command Server  │                      │  Query Server   │
│                 │                      │                 │
│ • Handlers      │                      │ • Handlers      │
│ • Commands      │                      │ • Queries       │
│ • Aggregates    │                      │ • Read Models   │
└────────┬────────┘                      └────────┬────────┘
         │                                        │
         ▼                                        ▼
┌─────────────────┐                      ┌─────────────────┐
│   Event Store   │─────Events──────────▶│   Read Models   │
│   (MongoDB)     │                      │   (MongoDB)     │
└─────────────────┘                      └─────────────────┘
         │
         ▼
┌─────────────────┐
│    RabbitMQ     │
│  (Event Bus)    │
└─────────────────┘
```

### Key Principles

1. **Commands** → Change state → Emit **Events**
2. **Events** → Update **Read Models** (projections)
3. **Queries** → Read from **Read Models** (never from aggregates)
4. **Handlers** → Orchestrate commands and queries

---

## 📁 Project Structure

```
source/src/
├── MyTelegram.Domain/              # Domain Layer (DDD + Event Sourcing)
│   ├── Aggregates/                 # Domain aggregates (UserAggregate, MessageAggregate, etc.)
│   ├── Commands/                   # Command definitions
│   ├── Events/                     # Domain events
│   ├── Sagas/                      # Long-running processes
│   └── ValueObjects/               # Value objects
│
├── MyTelegram.Messenger/           # Application Layer
│   ├── Handlers/LatestLayer/       # RPC handlers (orchestrate commands/queries)
│   │   ├── Messages/               # Message operations
│   │   ├── Channels/               # Channel operations
│   │   ├── Users/                  # User operations
│   │   └── ...                     # Other categories
│   ├── Services/                   # Application services
│   └── Converters/                 # Entity converters (ReadModel → TL Schema)
│
├── MyTelegram.ReadModel/           # Read Models (CQRS Read Side)
│   └── Impl/                       # Read model implementations
│
├── MyTelegram.ReadModel.Interfaces/# Read model interfaces
│
├── MyTelegram.QueryHandlers.MongoDB/ # Query handlers (read operations)
│
├── MyTelegram.Schema/              # TL Schema entities (AUTO-GENERATED)
│
├── MyTelegram.GatewayServer/       # MTProto gateway
│
├── MyTelegram.Messenger.CommandServer/ # Command server (write operations)
│
└── MyTelegram.Messenger.QueryServer/  # Query server (read operations)
```

**Important Notes:**
- `*.g.cs` files are **AUTO-GENERATED** from TL schema - don't edit manually
- Domain layer should be pure C# with no infrastructure dependencies
- Handlers should be thin - delegate to services and use Command/Query pattern

---

## 🔄 Implementation Workflow

### Phase 1: Research (ALWAYS START HERE)

```bash
# 1. Find TL schema constructor
/schema.jppgr.am search messages.sendMessage

# 2. Read official Telegram docs
https://core.telegram.org/method/messages.sendMessage

# 3. Check official client implementation
https://github.com/DrKLO/Telegram/search?q=sendMessage

# 4. Understand the domain
# - Is this a write operation (command) or read operation (query)?
# - What aggregate does it affect?
# - What events should be emitted?
# - What read models need to be updated?
```

### Phase 2: Design (Event Sourcing)

**For Write Operations (Commands):**

1. **Identify the aggregate** (User, Message, Channel, etc.)
2. **Define the command** (if not exists)
3. **Define domain events** (if not exists)
4. **Update read models** (event handlers)

**For Read Operations (Queries):**

1. **Identify read model** (UserReadModel, MessageReadModel, etc.)
2. **Create query** (if not exists)
3. **Create query handler** (if not exists)

### Phase 3: Implementation

**Write Operation Example:**

```csharp
// 1. Handler (orchestrates)
internal sealed class SendMessageHandler : RpcResultObjectHandler<RequestSendMessage, IUpdates>
{
    private readonly ICommandBus _commandBus;
    private readonly IQueryProcessor _queryProcessor;
    
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendMessage obj)
    {
        // 1. Validate
        if (string.IsNullOrWhiteSpace(obj.Message))
            RpcErrors.RpcErrors400.MessageEmpty.ThrowRpcError();
        
        // 2. Create command
        var command = new SendMessageCommand(
            MessageId.Create(idGenerator.NextId()),
            input.UserId,
            obj.Peer.ToPeer(),
            obj.Message,
            obj.RandomId
        );
        
        // 3. Execute command (will emit events)
        await _commandBus.PublishAsync(command, CancellationToken.None);
        
        // 4. Query updated read model
        var message = await _queryProcessor.ProcessAsync(
            new GetMessageQuery(command.MessageId)
        );
        
        // 5. Convert to TL schema and return
        return BuildUpdates(message);
    }
}

// 2. Command (in Domain layer)
public class SendMessageCommand : Command<MessageAggregate, MessageId>
{
    public long SenderUserId { get; }
    public Peer ToPeer { get; }
    public string Message { get; }
    public long RandomId { get; }
    
    public SendMessageCommand(
        MessageId aggregateId,
        long senderUserId,
        Peer toPeer,
        string message,
        long randomId)
        : base(aggregateId)
    {
        SenderUserId = senderUserId;
        ToPeer = toPeer;
        Message = message;
        RandomId = randomId;
    }
}

// 3. Aggregate (in Domain layer)
public class MessageAggregate : AggregateRoot<MessageAggregate, MessageId>
{
    public void SendMessage(long senderUserId, Peer toPeer, string message, long randomId)
    {
        // Business logic validation
        if (string.IsNullOrWhiteSpace(message))
            throw new DomainException("Message cannot be empty");
        
        // Emit event
        Emit(new MessageSentEvent(
            Id.Value,
            senderUserId,
            toPeer,
            message,
            randomId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        ));
    }
    
    // Apply event (update aggregate state)
    public void Apply(MessageSentEvent @event)
    {
        // Update aggregate state if needed
    }
}

// 4. Event (in Domain layer)
public class MessageSentEvent : AggregateEvent<MessageAggregate, MessageId>
{
    public long MessageId { get; }
    public long SenderUserId { get; }
    public Peer ToPeer { get; }
    public string Message { get; }
    public long RandomId { get; }
    public int Date { get; }
    
    public MessageSentEvent(
        long messageId,
        long senderUserId,
        Peer toPeer,
        string message,
        long randomId,
        int date)
    {
        MessageId = messageId;
        SenderUserId = senderUserId;
        ToPeer = toPeer;
        Message = message;
        RandomId = randomId;
        Date = date;
    }
}

// 5. Read Model (updated by event)
public class MessageReadModel : IMessageReadModel,
    IAmReadModelFor<MessageAggregate, MessageId, MessageSentEvent>
{
    public long MessageId { get; private set; }
    public long SenderUserId { get; private set; }
    public string Message { get; private set; }
    // ... other fields
    
    public Task ApplyAsync(
        IReadModelContext context,
        IDomainEvent<MessageAggregate, MessageId, MessageSentEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var @event = domainEvent.AggregateEvent;
        MessageId = @event.MessageId;
        SenderUserId = @event.SenderUserId;
        Message = @event.Message;
        // ... update other fields
        
        return Task.CompletedTask;
    }
}
```

**Read Operation Example:**

```csharp
// 1. Handler (simple query)
internal sealed class GetMessagesHandler : RpcResultObjectHandler<RequestGetMessages, IMessages>
{
    private readonly IQueryProcessor _queryProcessor;
    private readonly IObjectMapper _objectMapper;
    
    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestGetMessages obj)
    {
        // 1. Query read model
        var messages = await _queryProcessor.ProcessAsync(
            new GetMessagesQuery(obj.Id)
        );
        
        // 2. Convert to TL schema
        return new TMessages
        {
            Messages = new TVector<IMessage>(
                messages.Select(m => _objectMapper.Map<IMessage>(m))
            ),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>()
        };
    }
}

// 2. Query (in Queries project)
public class GetMessagesQuery : IQuery<IReadOnlyList<IMessageReadModel>>
{
    public IReadOnlyList<int> MessageIds { get; }
    
    public GetMessagesQuery(IReadOnlyList<int> messageIds)
    {
        MessageIds = messageIds;
    }
}

// 3. Query Handler (in QueryHandlers.MongoDB)
public class GetMessagesQueryHandler : IQueryHandler<GetMessagesQuery, IReadOnlyList<IMessageReadModel>>
{
    private readonly IMongoDatabase _database;
    
    public async Task<IReadOnlyList<IMessageReadModel>> ExecuteQueryAsync(
        GetMessagesQuery query,
        CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<MessageReadModel>("eventflow-messagereadmodel");
        var filter = Builders<MessageReadModel>.Filter.In(m => m.MessageId, query.MessageIds);
        
        return await collection.Find(filter).ToListAsync(cancellationToken);
    }
}
```

### Phase 4: Testing

```bash
# 1. Build
cd build/docker && ./1.build-messenger-command-server.sh

# 2. Restart services
docker-compose restart messenger-command-server messenger-query-server

# 3. Test with official Telegram client

# 4. Check logs
docker-compose logs -f messenger-command-server
docker-compose logs -f messenger-query-server

# 5. Verify MongoDB
docker-compose exec mongodb mongosh tg
db["eventflow-messagereadmodel"].findOne()
```

---

## 🎯 Handler Best Practices

### ✅ DO:

1. **Use Command/Query pattern:**
   ```csharp
   // ✅ GOOD - Write operation
   var command = new SendMessageCommand(...);
   await _commandBus.PublishAsync(command, CancellationToken.None);
   
   // ✅ GOOD - Read operation
   var messages = await _queryProcessor.ProcessAsync(new GetMessagesQuery(...));
   ```

2. **Use input.UserId from auth token:**
   ```csharp
   // ✅ GOOD
   var userId = input.UserId;
   
   // ❌ BAD - client can fake this
   var userId = obj.UserId;
   ```

3. **Initialize all TVector fields:**
   ```csharp
   // ✅ GOOD
   return new TMessages
   {
       Messages = new TVector<IMessage>(),
       Users = new TVector<IUser>(),
       Chats = new TVector<IChat>()
   };
   
   // ❌ BAD - NullReferenceException
   return new TMessages
   {
       Messages = null
   };
   ```

4. **Use RpcErrors for errors:**
   ```csharp
   // ✅ GOOD
   RpcErrors.RpcErrors400.MessageEmpty.ThrowRpcError();
   
   // ❌ BAD
   throw new Exception("Message is empty");
   ```

5. **Keep handlers thin:**
   ```csharp
   // ✅ GOOD - handler orchestrates
   protected override async Task<IUpdates> HandleCoreAsync(...)
   {
       await _validationService.ValidateAsync(...);
       var command = _commandFactory.CreateSendMessageCommand(...);
       await _commandBus.PublishAsync(command, CancellationToken.None);
       return await _responseBuilder.BuildUpdatesAsync(...);
   }
   
   // ❌ BAD - handler has business logic
   protected override async Task<IUpdates> HandleCoreAsync(...)
   {
       // 200 lines of business logic
   }
   ```

### ❌ DON'T:

1. **Don't bypass Event Sourcing:**
   ```csharp
   // ❌ BAD - direct MongoDB write
   await collection.InsertOneAsync(new BsonDocument { ... });
   
   // ✅ GOOD - use commands
   await _commandBus.PublishAsync(new CreateUserCommand(...), CancellationToken.None);
   ```

2. **Don't query aggregates directly:**
   ```csharp
   // ❌ BAD - aggregates are for writes only
   var aggregate = await _aggregateStore.LoadAsync<UserAggregate>(...);
   var username = aggregate.Username; // NO!
   
   // ✅ GOOD - query read models
   var user = await _queryProcessor.ProcessAsync(new GetUserQuery(...));
   var username = user.UserName;
   ```

3. **Don't use generic exceptions:**
   ```csharp
   // ❌ BAD
   catch (Exception ex) { }
   
   // ✅ GOOD
   catch (MongoException ex) { }
   catch (RpcErrorException ex) { }
   ```

---

## 📚 Claude Code Integration

### Available Prompts

Use these prompts from `.claude/prompts/`:

1. **create-handler.md** - Create new Telegram API handler
2. **refactor-service.md** - Refactor large service classes
3. **generate-tests.md** - Generate unit tests

### Usage Example

```
I need to implement messages.getHistory handler.

Use the create-handler.md prompt template.
```

### Custom Prompts

Create your own prompts in `.claude/prompts/` for common tasks:
- `fix-n-plus-one.md` - Fix N+1 query problems
- `add-validation.md` - Add input validation
- `extract-service.md` - Extract service from handler

---

## 🧪 Testing Strategy

### Test Pyramid

```
        ┌─────────────┐
        │   E2E (5%)  │  ← Full MTProto flow
        └─────────────┘
       ┌───────────────┐
       │Integration(15%)│ ← MongoDB, RabbitMQ
       └───────────────┘
      ┌─────────────────┐
      │  Unit (80%)     │  ← Handlers, Services, Domain
      └─────────────────┘
```

### Priority Order

1. **Domain layer** (Aggregates, Events) - Most important!
2. **Services** (Business logic)
3. **Handlers** (Orchestration)
4. **Query Handlers** (Read operations)
5. **Integration tests** (MongoDB, RabbitMQ)

### Test Template

```csharp
public class SendMessageHandlerTests
{
    private readonly Mock<ICommandBus> _commandBus;
    private readonly Mock<IQueryProcessor> _queryProcessor;
    private readonly SendMessageHandler _handler;
    
    [Fact]
    public async Task HandleCoreAsync_WithValidMessage_ShouldPublishCommand()
    {
        // Arrange
        var request = new RequestSendMessage { Message = "Hello" };
        
        // Act
        await _handler.HandleCoreAsync(input, request);
        
        // Assert
        _commandBus.Verify(
            x => x.PublishAsync(
                It.Is<SendMessageCommand>(c => c.Message == "Hello"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }
}
```

---

## 🗄️ MongoDB Collections

### Event Store Collections

- `eventflow-*aggregate` - Event streams (don't modify directly!)
- `eventflow-snapshots` - Aggregate snapshots

### Read Model Collections

- `eventflow-userreadmodel` - User read models
- `eventflow-messagereadmodel` - Message read models
- `eventflow-channelreadmodel` - Channel read models
- `eventflow-stickersetreadmodel` - Sticker set read models

### Custom Collections

- `fragment_collectibles` - Fragment NFT usernames/phones
- `call_sessions` - Voice/video call sessions
- `stories` - User/channel stories
- `businesschatlinks` - Business chat links

### Common Queries

```javascript
// Find user by ID
db["eventflow-userreadmodel"].findOne({ UserId: NumberLong("123456") })

// Find messages by sender
db["eventflow-messagereadmodel"].find({ SenderUserId: NumberLong("123456") })

// Find channel by username
db["eventflow-channelreadmodel"].findOne({ UserName: "testchannel" })
```

---

## 🚀 Performance Optimization

### MongoDB Indexes

```javascript
// User indexes
db["eventflow-userreadmodel"].createIndex({ "UserId": 1 })
db["eventflow-userreadmodel"].createIndex({ "PhoneNumber": 1 })
db["eventflow-userreadmodel"].createIndex({ "Usernames.Username": 1 })

// Message indexes
db["eventflow-messagereadmodel"].createIndex({ "MessageId": 1 })
db["eventflow-messagereadmodel"].createIndex({ "SenderUserId": 1, "Date": -1 })

// Channel indexes
db["eventflow-channelreadmodel"].createIndex({ "ChannelId": 1 })
db["eventflow-channelreadmodel"].createIndex({ "UserName": 1 })
```

### Avoid N+1 Queries

```csharp
// ❌ BAD - N+1 queries
foreach (var id in userIds)
{
    var user = await _queryProcessor.ProcessAsync(new GetUserQuery(id));
}

// ✅ GOOD - Single batch query
var users = await _queryProcessor.ProcessAsync(new GetUsersQuery(userIds));
```

### Caching Strategy

```csharp
// Use Redis for frequently accessed data
var cacheKey = $"user:{userId}";
var user = await _cache.GetOrCreateAsync(cacheKey, async () =>
{
    return await _queryProcessor.ProcessAsync(new GetUserQuery(userId));
}, TimeSpan.FromMinutes(5));
```

---

## 🔒 Security Best Practices

1. **Always use input.UserId** - Never trust client-provided user IDs
2. **Validate all inputs** - Check for null, empty, length, format
3. **Use RpcErrors** - Don't leak internal exceptions to clients
4. **Rate limiting** - Protect against spam/DDoS
5. **Secrets management** - Use environment variables, never hardcode

---

## 🐛 Common Mistakes

### 1. TVector = null

```csharp
// ❌ WRONG
return new TUpdates { Updates = null };

// ✅ CORRECT
return new TUpdates { Updates = new TVector<IUpdate>() };
```

### 2. Using request.UserId

```csharp
// ❌ WRONG - client can fake this
var userId = obj.UserId;

// ✅ CORRECT - from auth token
var userId = input.UserId;
```

### 3. Bypassing Event Sourcing

```csharp
// ❌ WRONG - direct MongoDB write
await collection.InsertOneAsync(doc);

// ✅ CORRECT - use commands
await _commandBus.PublishAsync(command, CancellationToken.None);
```

### 4. Querying aggregates

```csharp
// ❌ WRONG - aggregates are for writes
var aggregate = await _aggregateStore.LoadAsync<UserAggregate>(...);
var name = aggregate.FirstName;

// ✅ CORRECT - query read models
var user = await _queryProcessor.ProcessAsync(new GetUserQuery(...));
var name = user.FirstName;
```

### 5. Generic exception catching

```csharp
// ❌ WRONG
catch (Exception ex) { }

// ✅ CORRECT
catch (RpcErrorException ex) { }
catch (MongoException ex) { }
```

---

## 📖 Resources

- **Official Telegram API:** https://core.telegram.org/api
- **TL Schema:** https://core.telegram.org/schema
- **Methods:** https://core.telegram.org/methods
- **Android Client:** https://github.com/DrKLO/Telegram
- **Schema API:** https://schema.jppgr.am
- **EventFlow Docs:** https://github.com/eventflow/EventFlow

---

**Last Updated:** 2026-04-03

**Version:** 2.0 (Event Sourcing focused)
