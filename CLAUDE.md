# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Testgram** is a self-hosted C# implementation of the Telegram server-side API, forked from MyTelegram. It implements MTProto 2.0 protocol and supports API Layer 222.

### Technology Stack
- **Language**: C# (.NET 8+)
- **Architecture**: CQRS + Event Sourcing (EventFlow)
- **Database**: MongoDB (event store + read models)
- **Message Bus**: RabbitMQ
- **Cache**: Redis
- **Storage**: MinIO (S3-compatible)
- **Protocol**: MTProto 2.0
- **WebRTC**: Coturn (for voice/video calls)

## Critical Architecture Patterns

### 1. CQRS + Event Sourcing

**Command Side (Write)**:
- Commands → Aggregates → Domain Events → Event Store (MongoDB)
- Location: `source/src/MyTelegram.Domain/Aggregates/`
- Aggregates use EventFlow framework
- Events are immutable and append-only

**Query Side (Read)**:
- Domain Events → Projections → Read Models (MongoDB)
- Location: `source/src/MyTelegram.ReadModel*/`
- Read models are denormalized for fast queries
- Updated asynchronously via event handlers

**IMPORTANT**: 
- NEVER modify read models directly in command handlers
- ALWAYS emit domain events from aggregates
- Read models are eventually consistent

### 2. Event Flow Pattern

```
Client Request → RPC Handler → Aggregate → Domain Event → Event Store
                                                ↓
                                         Event Bus (RabbitMQ)
                                                ↓
                                    Event Handler → Read Model Update
```

**Key Classes**:
- `MyInMemorySnapshotAggregateRoot<>` - Base aggregate class
- `RpcResultObjectHandler<>` - Base RPC handler class
- Domain events in `MyTelegram.Domain/Aggregates/*/Events/`

### 3. Handler Organization

**RPC Handlers** (`source/src/MyTelegram.Messenger/Handlers/`):
```
LatestLayer/          # API Layer 222 (current)
├── Account/          # Account management
├── Auth/             # Authentication
├── Channels/         # Channel operations
├── Contacts/         # Contact management
├── Messages/         # Messaging
├── Phone/            # Voice/video calls
├── Photos/           # Photo management
├── Updates/          # Update delivery
└── Users/            # User operations

LayerN/               # Older API layers (backward compatibility)
```

**Handler Pattern**:
```csharp
internal sealed class MyHandler : RpcResultObjectHandler<RequestType, ResponseType>
{
    protected override async Task<ResponseType> HandleCoreAsync(
        IRequestInput input, 
        RequestType obj)
    {
        // 1. Validate input
        // 2. Load aggregate or query read model
        // 3. Execute business logic
        // 4. Return response
    }
}
```

## MongoDB Collections

### Event Store Collections
- `eventflow-*` - Event sourcing data (DO NOT modify directly)
- `eventflow-snapshots-*` - Aggregate snapshots

### Read Model Collections
- `call_sessions` - Voice/video call sessions
- `businesschatlinks` - Business chat links
- `quickreplys` - Quick reply shortcuts
- `star-gifts` - Star gift catalog
- `star-transactions` - Star balance transactions
- `saved-star-gifts` - User's received gifts
- `eventflow-userreadmodel` - User read model
- `eventflow-channelreadmodel` - Channel read model
- `eventflow-messagereadmodel` - Message read model

### Important Indexes
- `call_sessions`: CallId + AccessHash (unique), Date (TTL 30 days)
- Auto-created by `docker/compose/init-calls.sh` on startup

## Building and Running

### Local Development Build

```bash
# Build all projects
cd /root/testgram/build
./build.sh

# Output: ../out/local/<version>/
```

### Docker Build

```bash
cd /root/testgram/build/docker

# Set registry (optional)
export REGISTRY_URL="mytelegram"

# Build specific service
./1.build-messenger-command-server.sh
./2.build-messenger-query-server.sh
./5.build-gateway-server.sh
./6.build-auth-server.sh

# Or build all
./build-all-amd64.sh
```

### Running with Docker Compose

```bash
cd /root/testgram/docker/compose

# First time setup
cp .env.example .env
# Edit .env with your configuration

# Start all services
docker compose up -d

# View logs
docker compose logs -f messenger-command-server
docker compose logs -f messenger-query-server

# Restart specific service
docker compose restart messenger-command-server
```

### Service Dependencies

**Startup Order**:
1. MongoDB, Redis, RabbitMQ, MinIO
2. `call-init` (creates indexes, runs once)
3. `data-seeder` (seeds initial data)
4. `gateway-server`, `auth-server`
5. `messenger-command-server`, `messenger-query-server`

## Testing

```bash
cd /root/testgram/source

# Run all tests
dotnet test MyTelegram.slnx

# Run specific test project
dotnet test test/MyTelegram.Domain.Tests/
dotnet test test/MyTelegram.MTProto.Tests/
dotnet test test/MyTelegram.Schema.Tests/
```

## Common Development Tasks

### Adding a New RPC Handler

1. **Create handler file**:
```bash
# Location: source/src/MyTelegram.Messenger/Handlers/LatestLayer/<Category>/
# Example: MyNewFeatureHandler.cs
```

2. **Implement handler**:
```csharp
internal sealed class MyNewFeatureHandler 
    : RpcResultObjectHandler<RequestMyNewFeature, IMyResponse>
{
    private readonly IMongoDatabase _database; // If needed
    
    public MyNewFeatureHandler(IMongoDatabase database)
    {
        _database = database;
    }
    
    protected override async Task<IMyResponse> HandleCoreAsync(
        IRequestInput input, 
        RequestMyNewFeature obj)
    {
        var userId = input.UserId;
        
        // Validate
        if (obj.SomeField == null)
        {
            RpcErrors.RpcErrors400.FieldInvalid.ThrowRpcError();
        }
        
        // Business logic
        var collection = _database.GetCollection<BsonDocument>("mycollection");
        // ...
        
        return new TMyResponse { /* ... */ };
    }
}
```

3. **Handler is auto-registered** via dependency injection

### Working with Aggregates

**Loading an aggregate**:
```csharp
// Aggregates are loaded via EventFlow
// Usually done in command handlers, not RPC handlers
```

**Emitting events**:
```csharp
public class MyAggregate : MyInMemorySnapshotAggregateRoot<MyAggregate, MyId, MySnapshot>
{
    private readonly MyState _state = new();
    
    public void DoSomething(string value)
    {
        // Validate
        Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this);
        
        // Emit event
        Emit(new SomethingDoneEvent(_state.Id, value));
    }
}
```

### MongoDB Direct Access

**When to use**:
- Read-only queries in RPC handlers
- Business features (Business Chat Links, Quick Replies, etc.)
- Call sessions, star gifts, etc.

**Pattern**:
```csharp
private readonly IMongoDatabase _database;

public MyHandler(IMongoDatabase database)
{
    _database = database;
}

protected override async Task<IResponse> HandleCoreAsync(...)
{
    var collection = _database.GetCollection<BsonDocument>("mycollection");
    
    // Query
    var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
    var doc = await collection.Find(filter).FirstOrDefaultAsync();
    
    // Insert
    var newDoc = new BsonDocument
    {
        { "UserId", userId },
        { "Field", value },
        { "CreatedAt", DateTime.UtcNow }
    };
    await collection.InsertOneAsync(newDoc);
    
    // Update
    var update = Builders<BsonDocument>.Update
        .Set("Field", newValue)
        .Set("UpdatedAt", DateTime.UtcNow);
    await collection.UpdateOneAsync(filter, update);
    
    // Delete
    await collection.DeleteOneAsync(filter);
}
```

### RPC Error Handling

**Throwing errors**:
```csharp
// Predefined errors
RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
RpcErrors.RpcErrors404.PeerIdInvalid.ThrowRpcError();

// Custom message
RpcErrors.RpcErrors400.BadRequest.ThrowRpcError("Custom error message");
```

**Common error codes**:
- `400` - Bad Request (invalid input)
- `403` - Forbidden (permission denied)
- `404` - Not Found (resource doesn't exist)
- `500` - Internal Server Error

**Error definitions**: `source/src/MyTelegram.Domain.Shared/RpcErrors.g.cs`

## Voice & Video Calls

### Architecture

**Call Flow**:
1. `RequestCall` - Initiator creates call → MongoDB `call_sessions`
2. `AcceptCall` - Receiver accepts → Update state to "accepted"
3. `ConfirmCall` - Initiator confirms → Exchange keys, return WebRTC servers
4. `SendSignalingData` - Exchange ICE candidates via updates
5. `DiscardCall` - End call → Update state to "discarded"

**WebRTC Configuration**:
- **Required**: Own TURN/STUN server (Coturn)
- **No fallback**: Public STUN servers removed for security
- **Config**: `.env` → `App__WebRtcConnections__*`

**Call Session Document**:
```javascript
{
  CallId: Long,           // Unique call ID
  AccessHash: Long,       // Access hash for security
  CallerId: Long,         // Initiator user ID
  CalleeId: Long,         // Receiver user ID
  State: String,          // "requested", "accepted", "confirmed", "discarded"
  GAHash: Binary,         // Diffie-Hellman hash
  GA: Binary,             // Diffie-Hellman A
  GB: Binary,             // Diffie-Hellman B
  KeyFingerprint: Long,   // Encryption key fingerprint
  Video: Boolean,         // Video call flag
  Date: Int,              // Creation timestamp
  Duration: Int,          // Call duration (seconds)
  DiscardReason: String   // "missed", "hangup", "busy", etc.
}
```

**Indexes** (auto-created):
- `idx_callid_accesshash` (unique)
- `idx_callerid_date`
- `idx_calleeid_date`
- `idx_state_date`
- `idx_date` (TTL 30 days)

### Setting Up Coturn

```bash
# Install
sudo apt-get install coturn

# Configure /etc/turnserver.conf
listening-port=3478
external-ip=YOUR_SERVER_IP
realm=testgram.local
user=testgram:testgram123
lt-cred-mech
fingerprint

# Start
sudo systemctl start coturn

# Configure in .env
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123
```

## Telegram Business Features

### Business Chat Links

**Collection**: `businesschatlinks`

**Document Structure**:
```javascript
{
  UserId: Long,
  Slug: String,        // Unique link slug
  Title: String,
  Message: String,     // Pre-filled message
  Views: Int,
  CreatedAt: Date
}
```

**Handlers**:
- `CreateBusinessChatLinkHandler` - Create link (max 10 per user)
- `EditBusinessChatLinkHandler` - Edit existing link
- `DeleteBusinessChatLinkHandler` - Delete link
- `GetBusinessChatLinksHandler` - List user's links
- `ResolveBusinessChatLinkHandler` - Resolve slug to user

**Limits**: Max 10 links per user (enforced in handler)

### Business Settings

**Stored in**: `eventflow-userreadmodel` (UserFullReadModel)

**Fields**:
- `BusinessWorkHours` - Working hours configuration
- `BusinessLocation` - Business location
- `BusinessGreetingMessage` - Auto-reply for new chats
- `BusinessAwayMessage` - Auto-reply when away
- `BusinessIntro` - Business introduction

**Handlers**:
- `UpdateBusinessWorkHoursHandler`
- `UpdateBusinessLocationHandler`
- `UpdateBusinessGreetingMessageHandler`
- `UpdateBusinessAwayMessageHandler`
- `UpdateBusinessIntroHandler`

**Mapping**: `UserFullMapper.cs` maps read model to TL schema

## Quick Replies

### Collection Structure

**Collection**: `quickreplys`

**Document**:
```javascript
{
  UserId: Long,
  ShortcutId: Int,
  Shortcut: String,    // Name/trigger
  TopMessageId: Int,   // Latest message ID
  Count: Int,          // Message count
  Messages: [          // Array of messages
    {
      MessageId: Int,
      Message: String,
      Entities: Array,
      Media: Object
    }
  ]
}
```

**Handlers**:
- `GetQuickRepliesHandler` - List all shortcuts
- `GetQuickReplyMessagesHandler` - Get messages for shortcut
- `CheckQuickReplyShortcutHandler` - Check name availability
- `EditQuickReplyShortcutHandler` - Rename shortcut
- `DeleteQuickReplyShortcutHandler` - Delete shortcut
- `DeleteQuickReplyMessagesHandler` - Delete specific messages
- `ReorderQuickRepliesHandler` - Change order
- `SendQuickReplyMessagesHandler` - Send from shortcut

## History TTL (Auto-Delete)

**Purpose**: Automatically delete messages after specified time

**Handlers**:
- `GetDefaultHistoryTTLHandler` - Get default TTL
- `SetDefaultHistoryTTLHandler` - Set default for new chats
- `SetHistoryTTLHandler` - Set TTL for specific chat

**TTL Values**:
- `0` - Disabled
- `86400` - 1 day
- `604800` - 1 week
- `2592000` - 1 month
- Custom values in seconds

## Scripts and Utilities

### Reaction Seeder

**Purpose**: Download and import emoji reactions from Telegram

```bash
cd /root/testgram/scripts

# 1. Download reactions (requires Telegram account)
TG_API_ID=your_id TG_API_HASH=your_hash TG_PHONE=+1234567890 \
python3 seed_reactions.py --download

# 2. Import to MinIO + MongoDB
MONGO_URL=mongodb://localhost:27017 \
MINIO_ENDPOINT=localhost:9000 \
MINIO_ACCESS_KEY=key MINIO_SECRET_KEY=secret \
python3 seed_reactions.py --import

# 3. Generate handler with real document IDs
MONGO_URL=mongodb://localhost:27017 \
HANDLER_PATH=../source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetAvailableReactionsHandler.cs \
python3 seed_reactions.py --generate-handler

# 4. Rebuild messenger images
cd ../build/docker
./1.build-messenger-command-server.sh
./2.build-messenger-query-server.sh
```

### Call Indexes Setup

**Auto-setup** (runs on container start):
- `docker/compose/init-calls.sh` - Runs in `call-init` container
- Creates indexes if they don't exist
- Waits for MongoDB to be ready

**Manual setup**:
```bash
cd /root/testgram/scripts
./setup_call_indexes.sh

# Or via mongosh
docker compose exec mongodb mongosh tg < setup_call_indexes.js
```

### Start/Stop Scripts

```bash
cd /root/testgram/scripts

# Start all services (local development)
./start-all.sh

# Stop all services
./stop-all.bat  # Windows
# Or use docker compose down
```

## Configuration

### Environment Variables

**Critical settings** (`.env`):

```bash
# Database
ConnectionStrings__Default=mongodb://mongodb:27017
App__DatabaseName=tg
App__ReadModelDatabaseName=tg

# RabbitMQ
RabbitMQ__Connections__Default__HostName=rabbitmq
RabbitMQ__Connections__Default__Password=CHANGE_ME

# Security
App__AccessHashSecretKey=CHANGE_ME
App__EncryptionConfig__MessageKeys__0__Key=CHANGE_ME
App__EncryptionConfig__IndexKeys__0__Key=CHANGE_ME

# WebRTC (REQUIRED for calls)
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123

# Server
App__DcOptions__0__IpAddress=YOUR_SERVER_IP
App__DcOptions__0__Port=20443

# Testing
App__FixedVerifyCode=12345  # Fixed code for testing (empty in prod)
```

### Docker Compose Services

**Infrastructure**:
- `redis` - Cache (port 6379)
- `rabbitmq` - Message bus (ports 5672, 15672)
- `mongodb` - Database (port 27017)
- `minio` - Object storage (ports 9000, 9001)
- `coturn` - TURN/STUN server (port 3478)

**Application**:
- `call-init` - One-time setup (creates indexes)
- `data-seeder` - Seeds initial data
- `gateway-server` - MTProto gateway (ports 20443, 20543, 20643, 20644, 30443, 30444)
- `auth-server` - Authentication
- `messenger-command-server` - Command processing (CQRS write)
- `messenger-query-server` - Query processing (CQRS read)

## Troubleshooting

### Calls Not Working

1. **Check WebRTC config**:
```bash
docker compose exec messenger-command-server env | grep WebRtc
```

2. **Check Coturn**:
```bash
sudo systemctl status coturn
sudo tail -f /var/log/turnserver.log
```

3. **Check call sessions**:
```bash
docker compose exec mongodb mongosh tg
db.call_sessions.find().sort({Date: -1}).limit(5)
```

4. **Check indexes**:
```bash
docker compose logs call-init
db.call_sessions.getIndexes()
```

### MongoDB Issues

1. **Check connection**:
```bash
docker compose exec mongodb mongosh tg --eval "db.adminCommand('ping')"
```

2. **Check collections**:
```bash
docker compose exec mongodb mongosh tg --eval "db.getCollectionNames()"
```

3. **Check event store**:
```bash
db.getCollectionNames().filter(c => c.startsWith('eventflow-'))
```

### RabbitMQ Issues

1. **Check queues**:
```bash
docker compose exec rabbitmq rabbitmqctl list_queues
```

2. **Check connections**:
```bash
docker compose exec rabbitmq rabbitmqctl list_connections
```

3. **Management UI**: http://localhost:15672 (guest/guest)

### Build Issues

1. **Clean build**:
```bash
cd /root/testgram/scripts
./delete-bin-obj-folders.sh
cd ../build
./build.sh
```

2. **Check .NET version**:
```bash
dotnet --version  # Should be 8.0+
```

3. **Restore packages**:
```bash
cd /root/testgram/source
dotnet restore MyTelegram.slnx
```

## Code Style and Conventions

### Naming Conventions

- **Handlers**: `<Feature>Handler.cs` (e.g., `CreateBusinessChatLinkHandler.cs`)
- **Aggregates**: `<Entity>Aggregate.cs` (e.g., `UserAggregate.cs`)
- **Events**: `<Action><Entity>Event.cs` (e.g., `UserCreatedEvent.cs`)
- **Read Models**: `<Entity>ReadModel.cs` (e.g., `UserReadModel.cs`)
- **Collections**: lowercase with underscores (e.g., `call_sessions`, `businesschatlinks`)

### File Organization

```
Handlers/
  LatestLayer/
    <Category>/
      <Feature>Handler.cs

Domain/
  Aggregates/
    <Entity>/
      <Entity>Aggregate.cs
      <Entity>State.cs
      Events/
        <Event>Event.cs

ReadModel/
  Impl/
    <Entity>ReadModel.cs
```

### Error Handling

- **Always validate input** before processing
- **Use RpcErrors** for client-facing errors
- **Log exceptions** for debugging
- **Never expose internal errors** to clients

### MongoDB Patterns

- **Use BsonDocument** for flexible schemas
- **Create indexes** for frequently queried fields
- **Use TTL indexes** for auto-cleanup
- **Avoid large arrays** in documents (use separate collections)

## Performance Considerations

### Indexes

- **Always create indexes** for query fields
- **Use compound indexes** for multi-field queries
- **Monitor index usage**: `db.collection.stats()`

### Caching

- **Redis cache** for frequently accessed data
- **In-memory cache** for static data
- **Cache invalidation** via events

### Event Store

- **Snapshots** reduce replay time for large aggregates
- **Snapshot every N events** (configured in aggregate)
- **Archive old events** periodically

### Read Models

- **Denormalize** for query performance
- **Update asynchronously** via event handlers
- **Eventual consistency** is acceptable

## Security Best Practices

### Authentication

- **Access hash validation** for all operations
- **User ID from token** (never trust client)
- **Session management** via auth server

### Authorization

- **Check permissions** before operations
- **Validate ownership** of resources
- **Rate limiting** for sensitive operations

### Data Protection

- **Encrypt sensitive data** in database
- **Use HTTPS** for all connections
- **Secure WebRTC** with TURN authentication
- **Never log passwords** or tokens

### Input Validation

- **Validate all inputs** in handlers
- **Sanitize user content** before storage
- **Check limits** (message length, file size, etc.)
- **Prevent injection** attacks

## Deployment

### Production Checklist

- [ ] Set strong passwords in `.env`
- [ ] Configure real TURN/STUN server
- [ ] Set `App__FixedVerifyCode` to empty
- [ ] Enable HTTPS/TLS
- [ ] Configure firewall rules
- [ ] Set up monitoring and logging
- [ ] Configure backups (MongoDB, MinIO)
- [ ] Test call functionality
- [ ] Test message delivery
- [ ] Load test with expected traffic

### Monitoring

**Key metrics**:
- MongoDB connections and query time
- RabbitMQ queue depth
- Redis hit rate
- API response times
- Call success rate
- Error rates

**Logs**:
```bash
# Application logs
docker compose logs -f messenger-command-server
docker compose logs -f messenger-query-server

# Infrastructure logs
docker compose logs -f mongodb
docker compose logs -f rabbitmq
```

### Backup

**MongoDB**:
```bash
docker compose exec mongodb mongodump --db tg --out /backup
```

**MinIO**:
```bash
# Use MinIO client (mc)
mc mirror minio/tg-files /backup/files
```

## Additional Resources

- **Telegram API**: https://core.telegram.org/api
- **MTProto**: https://core.telegram.org/mtproto
- **EventFlow**: https://github.com/eventflow/EventFlow
- **MongoDB**: https://docs.mongodb.com/
- **RabbitMQ**: https://www.rabbitmq.com/documentation.html
- **Coturn**: https://github.com/coturn/coturn

## Getting Help

1. Check `docs/CALLS_SETUP.md` for call setup
2. Check logs: `docker compose logs -f <service>`
3. Check MongoDB: `docker compose exec mongodb mongosh tg`
4. Check this file for patterns and examples
5. Search codebase for similar implementations

## Important Notes

- **DO NOT** modify event store collections directly
- **DO NOT** skip event emission in aggregates
- **DO NOT** use public STUN servers (removed for security)
- **DO NOT** commit sensitive data (passwords, keys)
- **ALWAYS** validate user input
- **ALWAYS** check permissions before operations
- **ALWAYS** use RpcErrors for client errors
- **ALWAYS** test calls after WebRTC changes
