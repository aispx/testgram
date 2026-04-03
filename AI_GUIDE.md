# AI Development Guide for Testgram

**For:** Claude, GPT-4, Grok, DeepSeek, and other AI assistants  
**Purpose:** Understand project structure and development patterns  
**Last Updated:** 2026-04-03

---

## 🎯 Quick Context

**What is Testgram?**
- Self-hosted Telegram server (fork of MyTelegram)
- C# / .NET 10
- Architecture: CQRS + Event Sourcing (EventFlow framework)
- MTProto 2.0, API Layer 222
- Stack: MongoDB, RabbitMQ, Redis, MinIO, Coturn

**Project Scale:**
- 5,645 C# files
- 33 projects
- 792 Telegram API handlers
- Production-ready self-hosted Telegram server

---

## 📚 Documentation Hierarchy

**Read in this order:**

1. **CLAUDE_V2.md** - Primary development guide
   - Event Sourcing + CQRS patterns
   - Handler implementation examples
   - Best practices and common mistakes
   - Architecture diagrams

2. **IMPROVEMENT_ROADMAP.md** - 10-week improvement plan
   - Current state assessment
   - 5 priorities with detailed steps
   - Timeline and success metrics

3. **QUICK_START.md** - How to start implementing improvements
   - Week 1 tasks
   - Claude prompt usage examples
   - Refactoring workflow

4. **.claude/prompts/** - Specialized prompt templates
   - `create-handler.md` - Create new Telegram API handlers
   - `refactor-service.md` - Refactor God Classes

5. **CLAUDE.md** - Legacy guide (still useful)
   - Handler patterns
   - MongoDB collections reference
   - Common mistakes
   - Fragment API documentation

---

## 🏗️ Architecture Overview

### Event Sourcing + CQRS Pattern

```
Client Request
    ↓
Gateway Server (MTProto)
    ↓
    ├─→ Command Server (writes)
    │   ├─→ Handler (orchestrates)
    │   ├─→ Command → Aggregate → Events
    │   └─→ Events → Event Store (MongoDB)
    │
    └─→ Query Server (reads)
        ├─→ Handler (orchestrates)
        ├─→ Query → Read Model
        └─→ Read Model (MongoDB projections)
```

### Key Principles

1. **Commands change state, emit Events**
   - Use `ICommandBus.PublishAsync(command, ct)`
   - Never write directly to MongoDB
   - Always go through aggregates

2. **Events update Read Models**
   - Read models are projections of events
   - Updated automatically by EventFlow

3. **Queries read from Read Models**
   - Use `IQueryProcessor.ProcessAsync(query)`
   - Never query aggregates directly
   - Aggregates are for writes only

4. **Handlers orchestrate**
   - Thin handlers that delegate to services
   - Use Command/Query pattern
   - No business logic in handlers

---

## 📁 Project Structure

```
source/src/
├── MyTelegram.Domain/              # Domain Layer (pure C#)
│   ├── Aggregates/                 # UserAggregate, MessageAggregate, etc.
│   ├── Commands/                   # Command definitions
│   ├── Events/                     # Domain events
│   └── Sagas/                      # Long-running processes
│
├── MyTelegram.Messenger/           # Application Layer
│   ├── Handlers/LatestLayer/       # RPC handlers (ADD NEW HANDLERS HERE)
│   │   ├── Messages/               # Message operations
│   │   ├── Channels/               # Channel operations
│   │   ├── Users/                  # User operations
│   │   └── ...                     # Other categories
│   ├── Services/                   # Application services
│   └── Converters/                 # ReadModel → TL Schema converters
│
├── MyTelegram.ReadModel/           # Read Models (CQRS Read Side)
│   └── Impl/                       # UserReadModel, MessageReadModel, etc.
│
├── MyTelegram.QueryHandlers.MongoDB/ # Query handlers
│
├── MyTelegram.Schema/              # TL Schema entities (AUTO-GENERATED)
│
├── MyTelegram.Messenger.CommandServer/ # Command server (writes)
└── MyTelegram.Messenger.QueryServer/   # Query server (reads)
```

**Important:**
- `*.g.cs` files are AUTO-GENERATED - don't edit manually
- Domain layer has no infrastructure dependencies
- Handlers should be thin orchestrators

---

## 🔨 How to Implement Features

### Creating a New Handler

**Step 1: Research**
```bash
# Find TL schema
/schema.jppgr.am search messages.sendMessage

# Read official docs
https://core.telegram.org/method/messages.sendMessage

# Check Android client
https://github.com/DrKLO/Telegram/search?q=sendMessage
```

**Step 2: Use Claude Prompt**
```
I need to implement messages.sendMessage handler.

Use the create-handler.md prompt template from .claude/prompts/
```

**Step 3: Follow Pattern**

For **Write Operations** (commands):
```csharp
// Handler orchestrates
protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendMessage obj)
{
    // 1. Validate
    if (string.IsNullOrWhiteSpace(obj.Message))
        RpcErrors.RpcErrors400.MessageEmpty.ThrowRpcError();
    
    // 2. Create command
    var command = new SendMessageCommand(
        MessageId.Create(idGenerator.NextId()),
        input.UserId,  // ALWAYS use input.UserId, never obj.UserId
        obj.Peer.ToPeer(),
        obj.Message
    );
    
    // 3. Execute command (emits events)
    await _commandBus.PublishAsync(command, CancellationToken.None);
    
    // 4. Query updated read model
    var message = await _queryProcessor.ProcessAsync(new GetMessageQuery(command.MessageId));
    
    // 5. Convert to TL schema
    return BuildUpdates(message);
}
```

For **Read Operations** (queries):
```csharp
protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestGetMessages obj)
{
    // 1. Query read model
    var messages = await _queryProcessor.ProcessAsync(new GetMessagesQuery(obj.Id));
    
    // 2. Convert to TL schema
    return new TMessages
    {
        Messages = new TVector<IMessage>(messages.Select(m => _objectMapper.Map<IMessage>(m))),
        Users = new TVector<IUser>(),
        Chats = new TVector<IChat>()
    };
}
```

---

## ✅ Critical Rules

### DO:
1. ✅ Use `input.UserId` from auth token (never from request)
2. ✅ Use `ICommandBus` for write operations
3. ✅ Use `IQueryProcessor` for read operations
4. ✅ Initialize all `TVector<T>` fields (never null)
5. ✅ Use `RpcErrors` for error responses
6. ✅ Keep handlers thin (orchestrate, don't implement)

### DON'T:
1. ❌ Never use `obj.UserId` (client can fake it)
2. ❌ Never write directly to MongoDB
3. ❌ Never query aggregates (use read models)
4. ❌ Never leave `TVector<T>` as null
5. ❌ Never use generic `catch (Exception)`
6. ❌ Never edit `*.g.cs` files (auto-generated)

---

## 🗄️ MongoDB Collections

### Event Store (don't modify directly)
- `eventflow-*aggregate` - Event streams
- `eventflow-snapshots` - Aggregate snapshots

### Read Models (query these)
- `eventflow-userreadmodel` - Users
- `eventflow-messagereadmodel` - Messages
- `eventflow-channelreadmodel` - Channels
- `eventflow-stickersetreadmodel` - Sticker sets

### Custom Collections
- `fragment_collectibles` - Fragment NFT usernames/phones
- `call_sessions` - Voice/video calls
- `stories` - User/channel stories

---

## 🚨 Common Mistakes

### 1. TVector = null
```csharp
// ❌ WRONG
return new TUpdates { Updates = null };

// ✅ CORRECT
return new TUpdates { Updates = new TVector<IUpdate>() };
```

### 2. Using request.UserId
```csharp
// ❌ WRONG - client can fake
var userId = obj.UserId;

// ✅ CORRECT - from auth token
var userId = input.UserId;
```

### 3. Bypassing Event Sourcing
```csharp
// ❌ WRONG - direct MongoDB write
await collection.InsertOneAsync(doc);

// ✅ CORRECT - use commands
await _commandBus.PublishAsync(new CreateUserCommand(...), ct);
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

---

## 🔧 Refactoring Guidelines

### When to Refactor

**Refactor if:**
- Service class > 500 lines
- Method > 50 lines
- Class has > 5 responsibilities
- Hard to understand or test

**Use Claude:**
```
Refactor the service class: MessageAppService

Extract MessageValidationService from MessageAppService.

Use the refactor-service.md prompt template.
```

### Refactoring Pattern

```csharp
// Before (God Class)
public class MessageAppService
{
    // 851 lines
    // 15+ dependencies
    // Multiple responsibilities
}

// After (Focused Services)
public class MessageValidationService { /* validation only */ }
public class MessageSendingService { /* sending only */ }
public class MessageQueryService { /* queries only */ }

// Facade (backward compatibility)
public class MessageAppService
{
    private readonly IMessageSendingService _sending;
    private readonly IMessageQueryService _query;
    
    public Task SendAsync(...) => _sending.SendAsync(...);
    public Task<IReadOnlyList<IMessageReadModel>> GetAsync(...) => _query.GetAsync(...);
}
```

---

## 🎯 Current Priorities

**From IMPROVEMENT_ROADMAP.md:**

1. **Refactor God Classes** (2-3 weeks)
   - MessageAppService: 851 lines
   - CountryHelper: 3,443 lines
   - TimezoneHelper: 2,143 lines

2. **Implement Repository Pattern** (3-4 weeks)
   - Remove BsonDocument usage
   - Create IUserRepository, IMessageRepository, etc.

3. **Fix Exception Handling** (1-2 weeks)
   - Replace 233 generic catch blocks

4. **Add MongoDB Indexes** (1 week)
   - Optimize queries
   - Fix N+1 problems

5. **Add Monitoring** (1-2 weeks)
   - Prometheus + Grafana
   - Structured logging

---

## 💬 How to Ask for Help

### Good Request Format

```
I need to [task].

Context:
- File: [file path]
- Current code: [relevant snippet]
- What I want to achieve: [goal]
- What I've tried: [attempts]

Use [prompt template name] if applicable.
```

### Example

```
I need to implement messages.editMessage handler.

Context:
- Category: Messages
- Similar handler: SendMessageHandler
- TL Schema: messages.editMessage#48f71778

Use the create-handler.md prompt template.
```

---

## 📖 Additional Resources

- **EventFlow Docs:** https://github.com/eventflow/EventFlow
- **Telegram API:** https://core.telegram.org/api
- **TL Schema:** https://core.telegram.org/schema
- **Android Client:** https://github.com/DrKLO/Telegram

---

## 🚀 Quick Commands

```bash
# Build command server
cd build/docker && ./1.build-messenger-command-server.sh

# Build query server
cd build/docker && ./2.build-messenger-query-server.sh

# Restart services
docker-compose restart messenger-command-server messenger-query-server

# Check logs
docker-compose logs -f messenger-command-server

# MongoDB shell
docker-compose exec mongodb mongosh tg

# Find large files (God Classes)
find source/src -name "*.cs" -exec wc -l {} + | sort -rn | head -20
```

---

**Remember:** Always read CLAUDE_V2.md first for detailed patterns and examples!
