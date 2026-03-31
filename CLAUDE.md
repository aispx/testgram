# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## CRITICAL: Always Check External Resources

**BEFORE implementing ANY Telegram feature, you MUST:**

1. **Check official Telegram API documentation** at https://core.telegram.org/
2. **Read the specific method documentation** at https://core.telegram.org/method/<method_name>
3. **Check related API pages** for context and requirements
4. **Look at official Telegram client implementations** for reference

### Required Resources to Check

**When working on ANY feature, check these resources:**

#### Core API Documentation
- **Main API**: https://core.telegram.org/api
- **Methods Index**: https://core.telegram.org/methods
- **Types Index**: https://core.telegram.org/types
- **Schema**: https://core.telegram.org/schema
- **MTProto Protocol**: https://core.telegram.org/mtproto

#### Feature-Specific Documentation

**Stars & Payments**:
- https://core.telegram.org/api/stars
- https://core.telegram.org/api/payments
- https://core.telegram.org/api/paid-messages

**Gifts**:
- https://core.telegram.org/api/gifts
- https://core.telegram.org/api/gift-marketplaces
- https://core.telegram.org/api/gift-upgrades

**Business Features**:
- https://core.telegram.org/api/business
- https://core.telegram.org/api/business-intro
- https://core.telegram.org/api/business-chat-links

**Calls**:
- https://core.telegram.org/api/calls
- https://core.telegram.org/api/end-to-end/voice-calls
- https://core.telegram.org/api/end-to-end/video-calls

**Messages**:
- https://core.telegram.org/api/messages
- https://core.telegram.org/api/scheduled-messages
- https://core.telegram.org/api/quick-replies
- https://core.telegram.org/api/drafts

**Channels & Groups**:
- https://core.telegram.org/api/channel
- https://core.telegram.org/api/invites
- https://core.telegram.org/api/discussion
- https://core.telegram.org/api/forum

**Stories**:
- https://core.telegram.org/api/stories
- https://core.telegram.org/api/stories-stealth

**Bots**:
- https://core.telegram.org/api/bots
- https://core.telegram.org/api/bots/webapps
- https://core.telegram.org/api/bots/inline
- https://core.telegram.org/api/bots/payments

**Media**:
- https://core.telegram.org/api/files
- https://core.telegram.org/api/stickers
- https://core.telegram.org/api/animated-stickers
- https://core.telegram.org/api/custom-emoji

**Privacy & Security**:
- https://core.telegram.org/api/privacy
- https://core.telegram.org/api/end-to-end
- https://core.telegram.org/api/srp
- https://core.telegram.org/api/two-factor-auth

**Other Features**:
- https://core.telegram.org/api/reactions
- https://core.telegram.org/api/folders
- https://core.telegram.org/api/themes
- https://core.telegram.org/api/wallpapers
- https://core.telegram.org/api/translation
- https://core.telegram.org/api/premium
- https://core.telegram.org/api/links
- https://core.telegram.org/api/mentions

### How to Use External Resources

**Step 1: Read Method Documentation**

Example: Implementing `account.toggleUsername`
1. Go to: https://core.telegram.org/method/account.toggleUsername
2. Read parameters, return type, possible errors
3. Check related methods and types
4. Understand the business logic

**Step 2: Check API Context Pages**

Example: Implementing star gifts
1. Read: https://core.telegram.org/api/gifts
2. Read: https://core.telegram.org/api/gift-marketplaces
3. Read: https://core.telegram.org/api/gift-upgrades
4. Understand the complete flow

**Step 3: Look at Client Implementation**

Check official Telegram clients for reference:
- **Android**: https://github.com/DrKLO/Telegram
- **iOS**: https://github.com/TelegramMessenger/Telegram-iOS
- **Desktop**: https://github.com/telegramdesktop/tdesktop
- **Web**: https://github.com/morethanwords/tweb

Search for the method name in client code to see how it's used.

**Step 4: Check TL Schema**

- Current schema: https://core.telegram.org/schema
- Layer 222: https://corefork.telegram.org/schema/mtproto
- Compare with `source/src/MyTelegram.Schema/`

### Example Workflow

**Task**: Implement `messages.sendReaction`

1. **Read method docs**: https://core.telegram.org/method/messages.sendReaction
   - Parameters: `peer`, `msg_id`, `reaction`, `big`, `add_to_recent`
   - Returns: `Updates`
   - Errors: `MESSAGE_ID_INVALID`, `REACTION_INVALID`, etc.

2. **Read API context**: https://core.telegram.org/api/reactions
   - Understand reaction types (emoji, custom emoji)
   - Check reaction limits
   - Understand reaction updates

3. **Check client code**: Search "sendReaction" in Telegram-Android
   - See how UI handles reactions
   - Check validation logic
   - Understand user flow

4. **Implement handler**:
```csharp
internal sealed class SendReactionHandler 
    : RpcResultObjectHandler<RequestSendReaction, IUpdates>
{
    // Implementation based on documentation
}
```

5. **Test**: Compare behavior with official client

### When Implementing New Features

**ALWAYS follow this checklist**:

- [ ] Read official method documentation
- [ ] Check all related API pages
- [ ] Look at client implementation
- [ ] Understand complete user flow
- [ ] Check for edge cases in docs
- [ ] Implement with proper error handling
- [ ] Test against official client behavior
- [ ] Add MongoDB indexes if needed
- [ ] Update read models if needed

### Common Mistakes to Avoid

❌ **DON'T**: Implement based on method name alone
✅ **DO**: Read full documentation first

❌ **DON'T**: Guess parameter meanings
✅ **DO**: Check TL schema and docs

❌ **DON'T**: Ignore error codes in docs
✅ **DO**: Implement all documented errors

❌ **DON'T**: Skip client code review
✅ **DO**: See how official clients handle it

❌ **DON'T**: Assume simple implementation
✅ **DO**: Check for related features and dependencies

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

### Official Telegram Documentation

**Core API**:
- Main API: https://core.telegram.org/api
- Methods: https://core.telegram.org/methods
- Types: https://core.telegram.org/types
- Schema: https://core.telegram.org/schema
- MTProto: https://core.telegram.org/mtproto

**Feature Documentation** (ALWAYS check before implementing):
- Stars & Payments: https://core.telegram.org/api/stars
- Gifts: https://core.telegram.org/api/gifts
- Gift Marketplaces: https://core.telegram.org/api/gift-marketplaces
- Gift Upgrades: https://core.telegram.org/api/gift-upgrades
- Business Features: https://core.telegram.org/api/business
- Business Chat Links: https://core.telegram.org/api/business-chat-links
- Calls: https://core.telegram.org/api/calls
- Voice Calls: https://core.telegram.org/api/end-to-end/voice-calls
- Video Calls: https://core.telegram.org/api/end-to-end/video-calls
- Messages: https://core.telegram.org/api/messages
- Quick Replies: https://core.telegram.org/api/quick-replies
- Scheduled Messages: https://core.telegram.org/api/scheduled-messages
- Reactions: https://core.telegram.org/api/reactions
- Stories: https://core.telegram.org/api/stories
- Channels: https://core.telegram.org/api/channel
- Bots: https://core.telegram.org/api/bots
- WebApps: https://core.telegram.org/api/bots/webapps
- Stickers: https://core.telegram.org/api/stickers
- Custom Emoji: https://core.telegram.org/api/custom-emoji
- Premium: https://core.telegram.org/api/premium
- Folders: https://core.telegram.org/api/folders
- Privacy: https://core.telegram.org/api/privacy
- Two-Factor Auth: https://core.telegram.org/api/two-factor-auth

**Method Examples** (check specific methods):
- account.toggleUsername: https://core.telegram.org/method/account.toggleUsername
- messages.sendMessage: https://core.telegram.org/method/messages.sendMessage
- messages.sendReaction: https://core.telegram.org/method/messages.sendReaction
- phone.requestCall: https://core.telegram.org/method/phone.requestCall
- account.createBusinessChatLink: https://core.telegram.org/method/account.createBusinessChatLink
- messages.getQuickReplies: https://core.telegram.org/method/messages.getQuickReplies
- payments.getStarsTransactions: https://core.telegram.org/method/payments.getStarsTransactions

### Official Telegram Clients (for reference)

**Android**:
- Repository: https://github.com/DrKLO/Telegram
- Search methods: https://github.com/DrKLO/Telegram/search?q=sendReaction

**iOS**:
- Repository: https://github.com/TelegramMessenger/Telegram-iOS
- Search methods: https://github.com/TelegramMessenger/Telegram-iOS/search?q=sendReaction

**Desktop (TDesktop)**:
- Repository: https://github.com/telegramdesktop/tdesktop
- Search methods: https://github.com/telegramdesktop/tdesktop/search?q=sendReaction

**Web (TWeb)**:
- Repository: https://github.com/morethanwords/tweb
- Search methods: https://github.com/morethanwords/tweb/search?q=sendReaction

### How to Search Client Code

**Example**: Finding how `messages.sendReaction` is implemented

1. Go to Android client: https://github.com/DrKLO/Telegram
2. Search for "sendReaction": https://github.com/DrKLO/Telegram/search?q=sendReaction
3. Look at:
   - Request creation
   - Parameter validation
   - Response handling
   - UI updates
   - Error handling

4. Check multiple clients to understand complete flow

### Testgram Client Repositories

**For testing your implementation**:
- Android: https://github.com/glebxdlolreal/testgram-android
- Desktop: https://github.com/glebxdlolreal/testgram-tdesktop
- iOS: https://github.com/loyldg/mytelegram-iOS
- WebK: https://github.com/loyldg/mytelegram-webk
- WebA: https://github.com/loyldg/mytelegram-weba

### Other Resources

- EventFlow: https://github.com/eventflow/EventFlow
- MongoDB: https://docs.mongodb.com/
- RabbitMQ: https://www.rabbitmq.com/documentation.html
- Coturn: https://github.com/coturn/coturn
- WebRTC: https://webrtc.org/

## Implementation Checklist

**Before starting ANY feature implementation:**

1. [ ] Read method documentation at https://core.telegram.org/method/<method_name>
2. [ ] Read related API pages (stars, gifts, business, etc.)
3. [ ] Check TL schema at https://core.telegram.org/schema
4. [ ] Search implementation in official clients (Android, iOS, Desktop)
5. [ ] Understand complete user flow
6. [ ] Check error codes and edge cases
7. [ ] Plan MongoDB collections/indexes if needed
8. [ ] Plan read model updates if needed
9. [ ] Implement handler with proper validation
10. [ ] Test with official Telegram client
11. [ ] Test with Testgram client
12. [ ] Verify error handling
13. [ ] Check performance and indexes

**Example URLs to check for stars feature**:
- https://core.telegram.org/api/stars
- https://core.telegram.org/method/payments.getStarsTransactions
- https://core.telegram.org/method/payments.sendStarsForm
- https://github.com/DrKLO/Telegram/search?q=stars
- https://github.com/telegramdesktop/tdesktop/search?q=StarsTransaction

**Example URLs to check for gifts feature**:
- https://core.telegram.org/api/gifts
- https://core.telegram.org/api/gift-marketplaces
- https://core.telegram.org/api/gift-upgrades
- https://core.telegram.org/method/payments.getStarGifts
- https://github.com/DrKLO/Telegram/search?q=starGift
- https://github.com/telegramdesktop/tdesktop/search?q=StarGift

**Example URLs to check for business features**:
- https://core.telegram.org/api/business
- https://core.telegram.org/api/business-chat-links
- https://core.telegram.org/method/account.createBusinessChatLink
- https://core.telegram.org/method/account.updateBusinessWorkHours
- https://github.com/DrKLO/Telegram/search?q=businessChatLink

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
