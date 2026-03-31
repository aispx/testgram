# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with the Testgram codebase.

**Testgram** is a self-hosted C# implementation of the Telegram server-side API.
Fork of MyTelegram. Implements MTProto 2.0, API Layer 222.

**Technology Stack:**
- Language: C# (.NET 8+)
- Architecture: CQRS + Event Sourcing (EventFlow)
- Database: MongoDB (event store + read models)
- Message Bus: RabbitMQ
- Cache: Redis
- Storage: MinIO (S3-compatible)
- Protocol: MTProto 2.0
- WebRTC: Coturn (for voice/video calls)

## CRITICAL: Always Check External Resources Before Implementing

**BEFORE implementing ANY Telegram feature, you MUST:**

1. **Check official Telegram API documentation** at https://core.telegram.org/
2. **Read the specific method documentation** at https://core.telegram.org/method/<method_name>
3. **Check related API pages** for context and requirements
4. **Look at official Telegram client implementations** for reference

### Required Resources to Check

**Core API Documentation:**
- Main API: https://core.telegram.org/api
- Methods Index: https://core.telegram.org/methods
- Types Index: https://core.telegram.org/types
- Schema: https://core.telegram.org/schema
- MTProto Protocol: https://core.telegram.org/mtproto

**Feature-Specific Documentation:**

**Stars & Payments:**
- https://core.telegram.org/api/stars
- https://core.telegram.org/api/payments
- https://core.telegram.org/api/paid-messages

**Gifts:**
- https://core.telegram.org/api/gifts
- https://core.telegram.org/api/gift-marketplaces
- https://core.telegram.org/api/gift-upgrades

**Business Features:**
- https://core.telegram.org/api/business
- https://core.telegram.org/api/business-intro
- https://core.telegram.org/api/business-chat-links

**Calls:**
- https://core.telegram.org/api/calls
- https://core.telegram.org/api/end-to-end/voice-calls
- https://core.telegram.org/api/end-to-end/video-calls

**Messages:**
- https://core.telegram.org/api/messages
- https://core.telegram.org/api/scheduled-messages
- https://core.telegram.org/api/quick-replies
- https://core.telegram.org/api/drafts

**Channels & Groups:**
- https://core.telegram.org/api/channel
- https://core.telegram.org/api/invites
- https://core.telegram.org/api/discussion
- https://core.telegram.org/api/forum

**Stories:**
- https://core.telegram.org/api/stories
- https://core.telegram.org/api/stories-stealth

**Bots:**
- https://core.telegram.org/api/bots
- https://core.telegram.org/api/bots/webapps
- https://core.telegram.org/api/bots/inline
- https://core.telegram.org/api/bots/payments

**Media:**
- https://core.telegram.org/api/files
- https://core.telegram.org/api/stickers
- https://core.telegram.org/api/animated-stickers
- https://core.telegram.org/api/custom-emoji

**Privacy & Security:**
- https://core.telegram.org/api/privacy
- https://core.telegram.org/api/end-to-end
- https://core.telegram.org/api/srp
- https://core.telegram.org/api/two-factor-auth

**Other Features:**
- https://core.telegram.org/api/reactions
- https://core.telegram.org/api/folders
- https://core.telegram.org/api/themes
- https://core.telegram.org/api/wallpapers
- https://core.telegram.org/api/translation
- https://core.telegram.org/api/premium
- https://core.telegram.org/api/links
- https://core.telegram.org/api/mentions

### Official Telegram Clients (for reference)

**Android:**
- Repository: https://github.com/DrKLO/Telegram
- Search methods: https://github.com/DrKLO/Telegram/search?q=<method_name>

**iOS:**
- Repository: https://github.com/TelegramMessenger/Telegram-iOS
- Search methods: https://github.com/TelegramMessenger/Telegram-iOS/search?q=<method_name>

**Desktop (TDesktop):**
- Repository: https://github.com/telegramdesktop/tdesktop
- Search methods: https://github.com/telegramdesktop/tdesktop/search?q=<method_name>

**Web (TWeb):**
- Repository: https://github.com/morethanwords/tweb
- Search methods: https://github.com/morethanwords/tweb/search?q=<method_name>

### Implementation Workflow

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

Search for the method name in client code to see how it's used:
- Request creation
- Parameter validation
- Response handling
- UI updates
- Error handling

**Step 4: Check TL Schema**

- Current schema: https://core.telegram.org/schema
- Layer 222: https://corefork.telegram.org/schema/mtproto
- Compare with `source/src/MyTelegram.Schema/`

**Step 5: Implement Handler**

```csharp
internal sealed class MyHandler 
    : RpcResultObjectHandler<RequestMyMethod, IMyResponse>
{
    protected override async Task<IMyResponse> HandleCoreAsync(
        IRequestInput input, 
        RequestMyMethod obj)
    {
        // Implementation based on documentation
    }
}
```

**Step 6: Test**

Compare behavior with official client.

### Implementation Checklist

**Before starting ANY feature implementation:**

- [ ] Read method documentation at https://core.telegram.org/method/<method_name>
- [ ] Read related API pages (stars, gifts, business, etc.)
- [ ] Check TL schema at https://core.telegram.org/schema
- [ ] Search implementation in official clients (Android, iOS, Desktop)
- [ ] Understand complete user flow
- [ ] Check error codes and edge cases
- [ ] Plan MongoDB collections/indexes if needed
- [ ] Plan read model updates if needed
- [ ] Implement handler with proper validation
- [ ] Test with official Telegram client
- [ ] Test with Testgram client
- [ ] Verify error handling
- [ ] Check performance and indexes

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

## Architecture Patterns

### 1. CQRS + Event Sourcing

**Command Side (Write):**
- Commands → Aggregates → Domain Events → Event Store (MongoDB)
- Location: `source/src/MyTelegram.Domain/Aggregates/`
- Aggregates use EventFlow framework
- Events are immutable and append-only

**Query Side (Read):**
- Domain Events → Projections → Read Models (MongoDB)
- Location: `source/src/MyTelegram.ReadModel*/`
- Read models are denormalized for fast queries
- Updated asynchronously via event handlers

**CRITICAL RULES:**
- NEVER modify read models directly in command handlers
- ALWAYS emit domain events from aggregates
- Read models are eventually consistent
- DO NOT read from read model immediately after write

### 2. Event Flow Pattern

```
Client Request → RPC Handler → Aggregate → Domain Event → Event Store
                                                ↓
                                         Event Bus (RabbitMQ)
                                                ↓
                                    Event Handler → Read Model Update
```

**Key Classes:**
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
├── Stories/          # Stories
├── Updates/          # Update delivery
└── Users/            # User operations

LayerN/               # Older API layers (backward compatibility)
```

### 4. Handler Pattern

**Basic Handler:**
```csharp
internal sealed class MyHandler : RpcResultObjectHandler<RequestType, ResponseType>
{
    private readonly IMongoDatabase _database;
    
    public MyHandler(IMongoDatabase database)
    {
        _database = database;
    }
    
    protected override async Task<ResponseType> HandleCoreAsync(
        IRequestInput input, 
        RequestType obj)
    {
        // 1. Validate input
        if (obj.SomeField == null)
        {
            RpcErrors.RpcErrors400.FieldInvalid.ThrowRpcError();
        }
        
        // 2. Get userId from token (NEVER from request)
        var userId = input.UserId;
        
        // 3. Query or modify data
        var collection = _database.GetCollection<BsonDocument>("mycollection");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        
        // 4. Return response
        return new TMyResponse { /* ... */ };
    }
}
```

### 5. MongoDB Direct Access Pattern

**When to use:**
- Read-only queries in RPC handlers
- Business features (Business Chat Links, Quick Replies, etc.)
- Call sessions, star gifts, stories, etc.

**Pattern:**
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

### 6. RPC Error Handling

**Throwing errors:**
```csharp
// Predefined errors
RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
RpcErrors.RpcErrors404.PeerIdInvalid.ThrowRpcError();

// Custom message
RpcErrors.RpcErrors400.BadRequest.ThrowRpcError("Custom error message");
```

**Common error codes:**
- `400` - Bad Request (invalid input)
- `403` - Forbidden (permission denied)
- `404` - Not Found (resource doesn't exist)
- `500` - Internal Server Error

**Error definitions:** `source/src/MyTelegram.Domain.Shared/RpcErrors.g.cs`

**IMPORTANT:** Never throw generic exceptions in handlers - always use RpcErrors.

## Known Pitfalls & Anti-patterns

This section documents REAL mistakes made during implementation. Learn from them.

### Stories Implementation Pitfalls

**1. ExpireDate Type Mismatch (CRITICAL)**

❌ **WRONG:**
```csharp
var storyDocument = new StoryDocument
{
    ExpireDate = (int)expireDate  // Overflow! expireDate is long
};
```

✅ **CORRECT:**
```csharp
var storyDocument = new StoryDocument
{
    ExpireDate = expireDate  // Store as long in MongoDB
};

// When converting to TL schema:
var storyItem = new TStoryItem
{
    ExpireDate = (int)storyDocument.ExpireDate  // Cast to int for TL
};
```

**Why:** MongoDB stores `long`, but TL schema expects `int`. Cast only when returning to client.

**2. Deleted Flag Default Value**

❌ **WRONG:**
```csharp
var storyDocument = new StoryDocument
{
    Deleted = true  // Stories are deleted by default?!
};
```

✅ **CORRECT:**
```csharp
var storyDocument = new StoryDocument
{
    Deleted = false  // Stories are visible by default
};
```

**3. Empty TPhoto.Sizes**

❌ **WRONG:**
```csharp
var photo = new TPhoto
{
    Sizes = new TVector<IPhotoSize>()  // Empty! Client crashes
};
```

✅ **CORRECT:**
```csharp
var photo = new TPhoto
{
    Sizes = new TVector<IPhotoSize>
    {
        new TPhotoSize { Type = "s", W = 100, H = 100, Size = 1234 }
    }
};
```

**Why:** TPhoto.Sizes cannot be empty. Always include at least one size.

**4. Missing updateStoryID in Response**

❌ **WRONG:**
```csharp
return new TUpdates
{
    Updates = new TVector<IUpdate> { updateStory }  // Missing updateStoryID!
};
```

✅ **CORRECT:**
```csharp
var updateStoryId = new TUpdateStoryID
{
    Id = storyId,
    RandomId = obj.RandomId
};

var updateStory = new TUpdateStory
{
    Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
    Story = storyItem
};

return new TUpdates
{
    Updates = new TVector<IUpdate> { updateStoryId, updateStory }
};
```

**Why:** Client needs updateStoryID to map randomId to server-assigned storyId.

**5. Inconsistent Collection Names**

❌ **WRONG:**
```csharp
// In SendStoryHandler:
var collection = _database.GetCollection<StoryDocument>("stories");

// In GetStoriesHandler:
var collection = _database.GetCollection<StoryDocument>("story");  // Typo!
```

✅ **CORRECT:**
```csharp
// Use same collection name everywhere:
var collection = _database.GetCollection<StoryDocument>("stories");
```

**6. Exception in CanViewStory Returns Empty Result**

❌ **WRONG:**
```csharp
if (!CanViewStory(story, userId))
{
    throw new Exception("Cannot view story");  // Handler returns empty!
}
```

✅ **CORRECT:**
```csharp
if (!CanViewStory(story, userId))
{
    RpcErrors.RpcErrors403.StoryNotAvailable.ThrowRpcError();
}
```

**Why:** Generic exceptions cause handler to return empty response. Use RpcErrors.

### Phone (Calls) Implementation Pitfalls

**1. Hardcoded IP Address (CRITICAL)**

❌ **WRONG:**
```csharp
connections.Add(new TPhoneConnectionWebrtc
{
    Ip = "172.18.0.1",  // Hardcoded Docker internal IP!
    Port = 3478
});
```

✅ **CORRECT:**
```csharp
// Read from configuration:
var webRtcConfig = optionsAccessor.Value.WebRtcConnections[0];
connections.Add(new TPhoneConnectionWebrtc
{
    Ip = webRtcConfig.Ip,  // From .env: App__WebRtcConnections__0__Ip
    Port = webRtcConfig.Port
});
```

**2. Not Reading Environment Variables**

❌ **WRONG:**
```csharp
// Assuming config is always available
var ip = optionsAccessor.Value.WebRtcConnections[0].Ip;
```

✅ **CORRECT:**
```csharp
// Option 1: IOptions pattern (preferred)
public MyHandler(IOptions<MyTelegramMessengerServerOptions> optionsAccessor)
{
    var webRtcConnections = optionsAccessor.Value.WebRtcConnections;
}

// Option 2: Environment variable directly
var ip = Environment.GetEnvironmentVariable("App__WebRtcConnections__0__Ip");
```

**3. Using Public STUN Servers**

❌ **WRONG:**
```csharp
// Fallback to Google STUN
connections.Add(new TPhoneConnectionWebrtc
{
    Ip = "stun.l.google.com",
    Port = 19302,
    Stun = true
});
```

✅ **CORRECT:**
```csharp
// Only use own TURN/STUN server
if (webRtcConnections == null || webRtcConnections.Count == 0)
{
    throw new InvalidOperationException(
        "WebRTC connections not configured. " +
        "Please configure App__WebRtcConnections in .env file."
    );
}
```

**Why:** Security requirement - no external STUN servers.

### General Anti-patterns

**1. TVector<T> Cannot Be Null**

❌ **WRONG:**
```csharp
return new TUpdates
{
    Updates = null,  // NullReferenceException!
    Users = null,
    Chats = null
};
```

✅ **CORRECT:**
```csharp
return new TUpdates
{
    Updates = new TVector<IUpdate>(),
    Users = new TVector<IUser>(),
    Chats = new TVector<IChat>()
};
```

**Why:** TVector<T> is a class, but TL serialization expects empty vector, not null.

**2. Using input.UserId from Client Request**

❌ **WRONG:**
```csharp
protected override async Task<IResponse> HandleCoreAsync(
    IRequestInput input, 
    RequestMyMethod obj)
{
    var userId = obj.UserId;  // From client! Can be forged!
}
```

✅ **CORRECT:**
```csharp
protected override async Task<IResponse> HandleCoreAsync(
    IRequestInput input, 
    RequestMyMethod obj)
{
    var userId = input.UserId;  // From auth token - trusted
}
```

**Why:** input.UserId comes from authenticated session token. Never trust client-provided IDs.

**3. throw new NotImplementedException() in Handler**

❌ **WRONG:**
```csharp
protected override async Task<IResponse> HandleCoreAsync(...)
{
    throw new NotImplementedException();  // Client crashes!
}
```

✅ **CORRECT:**
```csharp
protected override async Task<IResponse> HandleCoreAsync(...)
{
    // Return minimal valid response:
    return new TMyResponse
    {
        // Fill required fields
    };
    
    // Or throw proper RPC error:
    RpcErrors.RpcErrors400.MethodNotSupported.ThrowRpcError();
}
```

**Why:** NotImplementedException causes client to crash. Use RpcErrors or return valid response.

**4. Reading Read Model Immediately After Write**

❌ **WRONG:**
```csharp
// Write to event store
await _commandBus.PublishAsync(command);

// Read immediately - data not there yet!
var readModel = await _queryProcessor.ProcessAsync(query);
```

✅ **CORRECT:**
```csharp
// Option 1: Return data from command result
var result = await _commandBus.PublishAsync(command);
return result.Data;

// Option 2: Use MongoDB directly for immediate consistency
var collection = _database.GetCollection<BsonDocument>("mycollection");
await collection.InsertOneAsync(doc);
var inserted = await collection.Find(filter).FirstOrDefaultAsync();
```

**Why:** Read models are eventually consistent. Event handlers may not have processed yet.

**5. Not Checking for Null in Optional Fields**

❌ **WRONG:**
```csharp
var caption = obj.Caption.Text;  // NullReferenceException if Caption is null
```

✅ **CORRECT:**
```csharp
var caption = obj.Caption?.Text ?? string.Empty;
```

**6. Forgetting to Set Date in Updates**

❌ **WRONG:**
```csharp
return new TUpdates
{
    Updates = new TVector<IUpdate> { update },
    Users = new TVector<IUser>(),
    Chats = new TVector<IChat>()
    // Missing Date!
};
```

✅ **CORRECT:**
```csharp
var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

return new TUpdates
{
    Updates = new TVector<IUpdate> { update },
    Users = new TVector<IUser>(),
    Chats = new TVector<IChat>(),
    Date = currentDate
};
```

### Summary: Top 10 Mistakes

1. **ExpireDate overflow** - Store as long, cast to int for TL
2. **Hardcoded IPs** - Always read from config
3. **TVector<T> = null** - Always use new TVector<T>()
4. **input.UserId from client** - Use input.UserId from token
5. **NotImplementedException** - Use RpcErrors instead
6. **Empty TPhoto.Sizes** - Always include at least one size
7. **Missing updateStoryID** - Include both updateStoryID and updateStory
8. **Read after write** - Read models are eventually consistent
9. **Null in optional fields** - Use null-conditional operator
10. **Missing Date in TUpdates** - Always set current timestamp

## MongoDB Collections Reference

### Complete Collection List

| Collection | Description | Who Writes | TTL | Indexes |
|------------|-------------|------------|-----|---------|
| `call_sessions` | Voice/video call sessions | Phone handlers | 30 days | CallId+AccessHash (unique), Date |
| `group_calls` | Group call sessions | Phone handlers | - | - |
| `stories` | User/channel stories | Stories handlers | - | OwnerPeerId, StoryId, Date |
| `businesschatlinks` | Business chat links | Account handlers | - | UserId, Slug (unique) |
| `quickreplys` | Quick reply shortcuts | Messages handlers | - | UserId, ShortcutId |
| `star-gifts` | Star gift catalog | Payments handlers | - | GiftId |
| `star-transactions` | Star balance transactions | Payments handlers | - | UserId, Date |
| `saved-star-gifts` | User's received gifts | Payments handlers | - | UserId, GiftId |
| `bot-verifications` | Bot verification status | Users handlers | - | BotId |
| `user-rating-overrides` | Manual user rating overrides | Users handlers | - | UserId |
| `eventflow-userreadmodel` | User read model | Event handlers | - | UserId (unique) |
| `eventflow-channelreadmodel` | Channel read model | Event handlers | - | ChannelId (unique) |
| `eventflow-messagereadmodel` | Message read model | Event handlers | - | MessageId, OwnerPeerId |
| `eventflow-dialogreadmodel` | Dialog read model | Event handlers | - | UserId, PeerId |
| `eventflow-stickersetreadmodel` | Sticker set read model | Event handlers | - | Id, ShortName |
| `eventflow-documentreadmodel` | Document read model | Event handlers | - | DocumentId |
| `eventflow-userinstalledstickersetreadmodel` | User's installed stickers | Event handlers | - | UserId, StickerSetId |
| `eventflow-*` | Event sourcing data | EventFlow | - | DO NOT MODIFY DIRECTLY |

### MongoDB Query Examples

**Find call session by CallId:**
```javascript
db.call_sessions.findOne({ CallId: NumberLong("123456789") })
```

**Find user's business chat links:**
```javascript
db.businesschatlinks.find({ UserId: NumberLong("123456") })
```

**Find user's quick replies:**
```javascript
db.quickreplys.find({ UserId: NumberLong("123456") })
```

**Find stories by owner:**
```javascript
db.stories.find({ 
    OwnerPeerId: NumberLong("123456"),
    Deleted: false 
}).sort({ Date: -1 })
```

**Count active calls:**
```javascript
db.call_sessions.countDocuments({ State: "confirmed" })
```

**Find expired stories:**
```javascript
db.stories.find({ 
    ExpireDate: { $lt: Math.floor(Date.now() / 1000) },
    Deleted: false 
})
```

**Check indexes on collection:**
```javascript
db.call_sessions.getIndexes()
```

**Analyze query performance:**
```javascript
db.call_sessions.find({ CallerId: NumberLong("123") }).explain("executionStats")
```

### Diagnostic Queries

**Check event store health:**
```javascript
db.getCollectionNames().filter(c => c.startsWith('eventflow-')).forEach(c => {
    print(c + ": " + db[c].countDocuments())
})
```

**Find recent errors in logs (if stored):**
```javascript
db.logs.find({ Level: "Error" }).sort({ Timestamp: -1 }).limit(10)
```

**Check collection sizes:**
```javascript
db.getCollectionNames().forEach(c => {
    var stats = db[c].stats()
    print(c + ": " + (stats.size / 1024 / 1024).toFixed(2) + " MB")
})
```

**Find orphaned data:**
```javascript
// Stories without owner in userreadmodel
db.stories.aggregate([
    {
        $lookup: {
            from: "eventflow-userreadmodel",
            localField: "OwnerPeerId",
            foreignField: "UserId",
            as: "owner"
        }
    },
    { $match: { owner: { $size: 0 } } }
])
```

## Code Patterns

### Atomic Counter Pattern

**Use case:** Generate unique IDs without race conditions

```csharp
// MongoDB atomic counter
var countersCollection = _database.GetCollection<BsonDocument>("counters");
var filter = Builders<BsonDocument>.Filter.Eq("_id", "storyId");
var update = Builders<BsonDocument>.Update.Inc("seq", 1);
var options = new FindOneAndUpdateOptions<BsonDocument>
{
    IsUpsert = true,
    ReturnDocument = ReturnDocument.After
};
var result = await countersCollection.FindOneAndUpdateAsync(filter, update, options);
var nextId = result["seq"].AsInt32;
```

### Upsert Pattern

**Use case:** Insert or update document atomically

```csharp
var collection = _database.GetCollection<BsonDocument>("mycollection");
var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
var update = Builders<BsonDocument>.Update
    .Set("Field", value)
    .Set("UpdatedAt", DateTime.UtcNow)
    .SetOnInsert("CreatedAt", DateTime.UtcNow);

var options = new ReplaceOptions { IsUpsert = true };
await collection.ReplaceOneAsync(filter, document, options);

// Or with UpdateOneAsync:
await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
```

### Batch Loading Pattern (Avoid N+1)

**Use case:** Load multiple related entities efficiently

❌ **WRONG (N+1 queries):**
```csharp
foreach (var story in stories)
{
    var user = await userCollection.Find(u => u.UserId == story.OwnerPeerId).FirstOrDefaultAsync();
    // Process story with user
}
```

✅ **CORRECT (1 query):**
```csharp
var ownerIds = stories.Select(s => s.OwnerPeerId).Distinct().ToList();
var filter = Builders<BsonDocument>.Filter.In("UserId", ownerIds);
var users = await userCollection.Find(filter).ToListAsync();
var userDict = users.ToDictionary(u => u["UserId"].AsInt64);

foreach (var story in stories)
{
    if (userDict.TryGetValue(story.OwnerPeerId, out var user))
    {
        // Process story with user
    }
}
```

### Reading Environment Variables

**Pattern 1: IOptions (Recommended)**
```csharp
public class MyHandler : RpcResultObjectHandler<TRequest, TResponse>
{
    private readonly MyTelegramMessengerServerOptions _options;
    
    public MyHandler(IOptions<MyTelegramMessengerServerOptions> optionsAccessor)
    {
        _options = optionsAccessor.Value;
    }
    
    protected override async Task<TResponse> HandleCoreAsync(...)
    {
        var webRtcIp = _options.WebRtcConnections[0].Ip;
        var dcIp = _options.DcOptions[0].IpAddress;
    }
}
```

**Pattern 2: Direct Environment Variable**
```csharp
var ip = Environment.GetEnvironmentVariable("App__WebRtcConnections__0__Ip");
var port = int.Parse(Environment.GetEnvironmentVariable("App__WebRtcConnections__0__Port") ?? "3478");
```

**Pattern 3: Configuration in Startup**
```csharp
// In Program.cs or Startup.cs
var webRtcIp = builder.Configuration["App:WebRtcConnections:0:Ip"];
```

### BuildProtocol Helper Pattern

**Use case:** Normalize PhoneCallProtocol with defaults

```csharp
private static TPhoneCallProtocol BuildProtocol(IPhoneCallProtocol? proto)
{
    var p = proto as TPhoneCallProtocol;
    return new TPhoneCallProtocol
    {
        UdpP2p = p?.UdpP2p ?? true,
        UdpReflector = p?.UdpReflector ?? true,
        MinLayer = p?.MinLayer ?? 65,
        MaxLayer = p?.MaxLayer ?? 92,
        LibraryVersions = new TVector<string> { "2.7.7" }
    };
}
```

### Pagination Pattern

**Use case:** Paginate large result sets

```csharp
var collection = _database.GetCollection<BsonDocument>("mycollection");
var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);

var totalCount = await collection.CountDocumentsAsync(filter);
var items = await collection.Find(filter)
    .Sort(Builders<BsonDocument>.Sort.Descending("Date"))
    .Skip(offset)
    .Limit(limit)
    .ToListAsync();

return new TMyResponse
{
    Items = ConvertItems(items),
    Count = (int)totalCount
};
```

### Transaction Pattern (When Needed)

**Use case:** Multiple operations must succeed or fail together

```csharp
using var session = await _database.Client.StartSessionAsync();
session.StartTransaction();

try
{
    await collection1.InsertOneAsync(session, doc1);
    await collection2.UpdateOneAsync(session, filter, update);
    
    await session.CommitTransactionAsync();
}
catch
{
    await session.AbortTransactionAsync();
    throw;
}
```

**Note:** Most operations don't need transactions. Use only when atomicity is critical.

### Soft Delete Pattern

**Use case:** Mark as deleted instead of removing

```csharp
// Mark as deleted
var update = Builders<BsonDocument>.Update
    .Set("Deleted", true)
    .Set("DeletedAt", DateTime.UtcNow);
await collection.UpdateOneAsync(filter, update);

// Query only non-deleted
var filter = Builders<BsonDocument>.Filter.And(
    Builders<BsonDocument>.Filter.Eq("UserId", userId),
    Builders<BsonDocument>.Filter.Ne("Deleted", true)
);
```

### TTL Index Pattern

**Use case:** Auto-delete old documents

```javascript
// In MongoDB shell or init script:
db.call_sessions.createIndex(
    { "Date": 1 },
    { expireAfterSeconds: 2592000 }  // 30 days
)
```

```csharp
// In C# (if creating indexes programmatically):
var keys = Builders<BsonDocument>.IndexKeys.Ascending("Date");
var options = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) };
await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys, options));
```

## TL Schema Quick Reference

### Understanding TL Schema

**TL (Type Language)** defines Telegram's API types and methods.

**Schema location:**
- Official: https://core.telegram.org/schema
- Local: `source/src/MyTelegram.Schema/`

### Required vs Optional Fields

**In TL schema:**
```tl
storyItem#79b26a24 flags:# pinned:flags.5?true public:flags.7?true 
    close_friends:flags.8?true min:flags.9?true noforwards:flags.10?true 
    edited:flags.11?true contacts:flags.12?true selected_contacts:flags.13?true 
    out:flags.16?true id:int date:int from_id:Peer expire_date:int 
    caption:flags.0?string entities:flags.1?Vector<MessageEntity> 
    media:MessageMedia media_areas:flags.14?Vector<MediaArea> 
    privacy:flags.2?Vector<PrivacyRule> views:flags.3?StoryViews 
    sent_reaction:flags.15?Reaction = StoryItem;
```

**Reading the schema:**
- `flags:#` - Bitfield for optional fields
- `pinned:flags.5?true` - Optional boolean (bit 5)
- `caption:flags.0?string` - Optional string (bit 0)
- `id:int` - Required field (no flags)
- `expire_date:int` - Required field

**In C# code:**
```csharp
var storyItem = new TStoryItem
{
    // Required fields - MUST be set
    Id = storyId,
    Date = currentDate,
    ExpireDate = (int)expireDate,  // Cast long to int for TL
    FromId = new TPeerUser { UserId = userId },
    Media = media,
    
    // Optional fields - set flag bit if present
    Caption = caption,  // Sets flags.0
    Pinned = true,      // Sets flags.5
    Views = views       // Sets flags.3
};
```

### Common TL Type Mappings

| TL Type | C# Type | Notes |
|---------|---------|-------|
| `int` | `int` | 32-bit signed |
| `long` | `long` | 64-bit signed |
| `double` | `double` | 64-bit float |
| `string` | `string` | UTF-8 string |
| `bytes` | `byte[]` | Binary data |
| `true` | `bool` | Flag-only boolean |
| `Bool` | `bool` | Explicit boolean |
| `Vector<T>` | `TVector<T>` | Array/list |
| `flags.N?T` | `T?` or `T` | Optional field |

### Finding Types in Client Code

**Android (Java):**
```bash
# Find TL class definition
grep -A 50 "class TL_storyItem\b" TelegramMessenger/jni/tgnet/TLRPC.java

# Find method implementation
grep -A 100 "TL_stories_sendStory" TelegramMessenger/src/main/java/
```

**Desktop (C++):**
```bash
# Find TL type
grep -A 30 "MTPDstoryItem" Telegram/SourceFiles/mtproto/scheme/api.tl

# Find method usage
grep -r "MTPstories_SendStory" Telegram/SourceFiles/
```

**Web (TypeScript):**
```bash
# Find type definition
grep -A 20 "storyItem:" tweb/src/layer.d.ts

# Find method call
grep -r "invokeApi('stories.sendStory'" tweb/src/
```

### Top TL Schema Errors

**1. Null in required field:**
```csharp
// TL: id:int (required)
var item = new TStoryItem { Id = null };  // ERROR!
```

**2. Int overflow:**
```csharp
// TL: expire_date:int (32-bit)
long expireDate = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
var item = new TStoryItem { ExpireDate = expireDate };  // ERROR! Overflow
// Correct: ExpireDate = (int)expireDate
```

**3. Empty Vector:**
```csharp
// TL: sizes:Vector<PhotoSize> (required, non-empty)
var photo = new TPhoto { Sizes = new TVector<IPhotoSize>() };  // ERROR!
// Must have at least one size
```

**4. Wrong type variant:**
```csharp
// TL: Peer = PeerUser | PeerChat | PeerChannel
var peer = new TPeer();  // ERROR! TPeer is abstract
// Correct: new TPeerUser { UserId = id }
```

**5. Missing flags calculation:**
```csharp
// TL handles flags automatically in C# implementation
// But be aware: setting optional field = setting flag bit
var item = new TStoryItem
{
    Caption = "text"  // Automatically sets flags.0
};
```

### Checking Schema Compatibility

**Compare local schema with official:**
```bash
# Download current schema
curl https://core.telegram.org/schema -o /tmp/official.tl

# Compare with local
diff /tmp/official.tl source/src/MyTelegram.Schema/layer222.tl
```

**Check if method exists in schema:**
```bash
grep "stories.sendStory" source/src/MyTelegram.Schema/layer222.tl
```

**Find all methods in category:**
```bash
grep "^stories\." source/src/MyTelegram.Schema/layer222.tl
```

## Handler → Collection Mapping

Quick reference for finding which handler uses which MongoDB collection.

### Stories

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `stories.sendStory` | `SendStoryHandler.cs` | `stories` | Creates new story |
| `stories.editStory` | `EditStoryHandler.cs` | `stories` | Updates existing story |
| `stories.deleteStories` | `DeleteStoriesHandler.cs` | `stories` | Soft delete (Deleted=true) |
| `stories.togglePinned` | `TogglePinnedHandler.cs` | `stories` | Updates Pinned field |
| `stories.getAllStories` | `GetAllStoriesHandler.cs` | `stories` | Queries by OwnerPeerId |
| `stories.getPinnedStories` | `GetPinnedStoriesHandler.cs` | `stories` | Queries Pinned=true |
| `stories.getStoriesArchive` | `GetStoriesArchiveHandler.cs` | `stories` | Queries archived stories |
| `stories.getStoriesViews` | `GetStoriesViewsHandler.cs` | `story_views` | View statistics |
| `stories.incrementStoryViews` | `IncrementStoryViewsHandler.cs` | `stories` | Increments ViewsCount |

### Phone (Calls)

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `phone.requestCall` | `RequestCallHandler.cs` | `call_sessions` | Creates call session |
| `phone.acceptCall` | `AcceptCallHandler.cs` | `call_sessions` | Updates State to "accepted" |
| `phone.confirmCall` | `ConfirmCallHandler.cs` | `call_sessions` | Updates State to "confirmed" |
| `phone.discardCall` | `DiscardCallHandler.cs` | `call_sessions` | Updates State to "discarded" |
| `phone.receivedCall` | `ReceivedCallHandler.cs` | `call_sessions` | Marks call as received |
| `phone.sendSignalingData` | `SendSignalingDataHandler.cs` | `call_sessions` | Stores ICE candidates |
| `phone.saveCallDebug` | `SaveCallDebugHandler.cs` | `call_sessions` | Saves debug info |
| `phone.getCallConfig` | `GetCallConfigHandler.cs` | - | Returns WebRTC config |
| `phone.createGroupCall` | `CreateGroupCallHandler.cs` | `group_calls` | Creates group call |
| `phone.joinGroupCall` | `JoinGroupCallHandler.cs` | `group_calls` | Adds participant |
| `phone.leaveGroupCall` | `LeaveGroupCallHandler.cs` | `group_calls` | Removes participant |
| `phone.getGroupCall` | `GetGroupCallHandler.cs` | `group_calls` | Queries group call |

### Business Features

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `account.createBusinessChatLink` | `CreateBusinessChatLinkHandler.cs` | `businesschatlinks` | Max 10 per user |
| `account.editBusinessChatLink` | `EditBusinessChatLinkHandler.cs` | `businesschatlinks` | Updates link |
| `account.deleteBusinessChatLink` | `DeleteBusinessChatLinkHandler.cs` | `businesschatlinks` | Deletes link |
| `account.getBusinessChatLinks` | `GetBusinessChatLinksHandler.cs` | `businesschatlinks` | Lists user's links |
| `account.resolveBusinessChatLink` | `ResolveBusinessChatLinkHandler.cs` | `businesschatlinks` | Resolves slug |
| `account.updateBusinessWorkHours` | `UpdateBusinessWorkHoursHandler.cs` | `eventflow-userreadmodel` | Updates user profile |
| `account.updateBusinessLocation` | `UpdateBusinessLocationHandler.cs` | `eventflow-userreadmodel` | Updates user profile |
| `account.updateBusinessGreetingMessage` | `UpdateBusinessGreetingMessageHandler.cs` | `eventflow-userreadmodel` | Updates user profile |
| `account.updateBusinessAwayMessage` | `UpdateBusinessAwayMessageHandler.cs` | `eventflow-userreadmodel` | Updates user profile |
| `account.updateBusinessIntro` | `UpdateBusinessIntroHandler.cs` | `eventflow-userreadmodel` | Updates user profile |

### Quick Replies

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `messages.getQuickReplies` | `GetQuickRepliesHandler.cs` | `quickreplys` | Lists all shortcuts |
| `messages.getQuickReplyMessages` | `GetQuickReplyMessagesHandler.cs` | `quickreplys` | Gets messages for shortcut |
| `messages.sendQuickReplyMessages` | `SendQuickReplyMessagesHandler.cs` | `quickreplys` | Sends from shortcut |
| `messages.editQuickReplyShortcut` | `EditQuickReplyShortcutHandler.cs` | `quickreplys` | Renames shortcut |
| `messages.deleteQuickReplyShortcut` | `DeleteQuickReplyShortcutHandler.cs` | `quickreplys` | Deletes shortcut |
| `messages.deleteQuickReplyMessages` | `DeleteQuickReplyMessagesHandler.cs` | `quickreplys` | Deletes messages |
| `messages.reorderQuickReplies` | `ReorderQuickRepliesHandler.cs` | `quickreplys` | Changes order |
| `messages.checkQuickReplyShortcut` | `CheckQuickReplyShortcutHandler.cs` | `quickreplys` | Checks availability |

### Payments & Stars

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `payments.getStarsTransactions` | `GetStarsTransactionsHandler.cs` | `star-transactions` | User's transaction history |
| `payments.sendStarsForm` | `SendStarsFormHandler.cs` | `star-transactions` | Creates transaction |
| `payments.getStarGifts` | `GetStarGiftsHandler.cs` | `star-gifts` | Gift catalog |
| `payments.sendStarGift` | `SendStarGiftHandler.cs` | `saved-star-gifts` | Sends gift to user |
| `payments.getUserStarGifts` | `GetUserStarGiftsHandler.cs` | `saved-star-gifts` | User's received gifts |

### Stickers

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `stickers.createStickerSet` | `CreateStickerSetHandler.cs` | `eventflow-stickersetreadmodel` | Creates set |
| `stickers.addStickerToSet` | `AddStickerToSetHandler.cs` | `eventflow-stickersetreadmodel` | Adds sticker |
| `stickers.removeStickerFromSet` | `RemoveStickerFromSetHandler.cs` | `eventflow-stickersetreadmodel` | Removes sticker |
| `stickers.changeStickerPosition` | `ChangeStickerPositionHandler.cs` | `eventflow-stickersetreadmodel` | Reorders |
| `stickers.changeStickerSet` | `ChangeStickerHandler.cs` | `eventflow-stickersetreadmodel` | Updates sticker |
| `stickers.renameStickerSet` | `RenameStickerSetHandler.cs` | `eventflow-stickersetreadmodel` | Renames set |
| `stickers.setStickerSetThumb` | `SetStickerSetThumbHandler.cs` | `eventflow-stickersetreadmodel` | Sets thumbnail |

### History TTL

| TL Method | Handler File | MongoDB Collection | Notes |
|-----------|--------------|-------------------|-------|
| `messages.getDefaultHistoryTTL` | `GetDefaultHistoryTTLHandler.cs` | `eventflow-userreadmodel` | User's default TTL |
| `messages.setDefaultHistoryTTL` | `SetDefaultHistoryTTLHandler.cs` | `eventflow-userreadmodel` | Sets default TTL |
| `messages.setHistoryTTL` | `SetHistoryTTLHandler.cs` | `eventflow-dialogreadmodel` | Sets chat TTL |

## Debug Workflow

### Problem: Handler Returns Empty Response

**Symptoms:**
- Client receives empty response
- No error message
- Handler seems to execute

**Debug steps:**

1. **Check logs:**
```bash
docker compose logs -f messenger-command-server | grep -i error
docker compose logs -f messenger-query-server | grep -i error
```

2. **Add debug logging in handler:**
```csharp
protected override async Task<IResponse> HandleCoreAsync(...)
{
    Console.WriteLine($"[DEBUG] Handler called: UserId={input.UserId}");
    
    try
    {
        var result = await DoSomething();
        Console.WriteLine($"[DEBUG] Result: {result}");
        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Exception: {ex}");
        throw;
    }
}
```

3. **Check MongoDB directly:**
```bash
docker compose exec mongodb mongosh tg
db.mycollection.findOne({ UserId: NumberLong("123456") })
```

4. **Common causes:**
- Generic exception thrown (use RpcErrors instead)
- TVector<T> set to null (use new TVector<T>())
- Required TL field not set
- Wrong collection name

### Problem: Handler Not Called At All

**Symptoms:**
- No logs from handler
- Client times out or gets "method not found"

**Debug steps:**

1. **Check handler is registered:**
```bash
# Handler must be internal sealed class
grep "internal sealed class MyHandler" source/src/MyTelegram.Messenger/Handlers/LatestLayer/
```

2. **Check namespace:**
```csharp
// Must be in correct namespace
namespace MyTelegram.Messenger.Handlers.LatestLayer.Category;
```

3. **Check build errors:**
```bash
cd source
dotnet build MyTelegram.slnx 2>&1 | grep -i error
```

4. **Rebuild and restart:**
```bash
cd build/docker
./1.build-messenger-command-server.sh
./2.build-messenger-query-server.sh
cd ../../docker/compose
docker compose restart messenger-command-server messenger-query-server
```

5. **Check TL schema mapping:**
```bash
# Verify method exists in schema
grep "myMethod" source/src/MyTelegram.Schema/layer222.tl
```

### Problem: Client Crashes on Response

**Symptoms:**
- Handler executes successfully
- Client crashes or shows error
- Logs show no error

**Debug steps:**

1. **Check TVector fields:**
```csharp
// WRONG:
return new TUpdates { Updates = null };

// CORRECT:
return new TUpdates { Updates = new TVector<IUpdate>() };
```

2. **Check required TL fields:**
```csharp
// Check TL schema for required fields
// Example: TPhoto requires non-empty Sizes
var photo = new TPhoto
{
    Id = photoId,
    AccessHash = accessHash,
    Sizes = new TVector<IPhotoSize>
    {
        new TPhotoSize { Type = "s", W = 100, H = 100, Size = 1234 }
    }
};
```

3. **Check type casts:**
```csharp
// WRONG: long to int overflow
ExpireDate = expireDate  // expireDate is long, TL expects int

// CORRECT:
ExpireDate = (int)expireDate
```

4. **Test with official client:**
- Compare response with official Telegram server
- Use Telegram Desktop with debug logging
- Check network inspector in browser clients

### Problem: Calls Not Working

**Symptoms:**
- Call initiated but no connection
- "Connecting..." forever
- No audio/video

**Debug steps:**

1. **Check WebRTC configuration:**
```bash
docker compose exec messenger-command-server env | grep WebRtc
# Should show: App__WebRtcConnections__0__Ip=YOUR_IP
```

2. **Check Coturn status:**
```bash
sudo systemctl status coturn
sudo tail -f /var/log/turnserver.log
```

3. **Check call session in MongoDB:**
```bash
docker compose exec mongodb mongosh tg
db.call_sessions.find().sort({Date: -1}).limit(5).pretty()
```

4. **Check call indexes:**
```bash
docker compose logs call-init
db.call_sessions.getIndexes()
```

5. **Test TURN server:**
```bash
# Use trickle ICE test: https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/
# Enter your TURN server credentials
```

6. **Common issues:**
- Hardcoded IP instead of config
- WebRTC config not set in .env
- Coturn not running
- Firewall blocking UDP 3478
- No TURN credentials

### Problem: MongoDB Query Slow

**Symptoms:**
- Handler takes long time
- Timeout errors
- High CPU on MongoDB

**Debug steps:**

1. **Check query execution:**
```javascript
db.mycollection.find({ UserId: NumberLong("123") }).explain("executionStats")
```

2. **Check if index is used:**
```javascript
// Look for "IXSCAN" in executionStats
// If you see "COLLSCAN", you need an index
```

3. **Create missing index:**
```javascript
db.mycollection.createIndex({ UserId: 1 })
```

4. **Check index usage:**
```javascript
db.mycollection.getIndexes()
db.mycollection.stats()
```

5. **Optimize query:**
```csharp
// Use projection to fetch only needed fields
var projection = Builders<BsonDocument>.Projection
    .Include("UserId")
    .Include("Field1")
    .Exclude("_id");
var doc = await collection.Find(filter).Project(projection).FirstOrDefaultAsync();
```

### Problem: Event Not Processed

**Symptoms:**
- Command succeeds
- Read model not updated
- Data inconsistent

**Debug steps:**

1. **Check RabbitMQ:**
```bash
docker compose exec rabbitmq rabbitmqctl list_queues
# Look for growing queue
```

2. **Check event store:**
```bash
docker compose exec mongodb mongosh tg
db.getCollectionNames().filter(c => c.startsWith('eventflow-'))
```

3. **Check event handler logs:**
```bash
docker compose logs -f messenger-query-server | grep -i event
```

4. **Manually trigger read model update:**
```bash
# Restart query server to reprocess events
docker compose restart messenger-query-server
```

### Problem: Build Fails

**Symptoms:**
- Docker build fails
- Compilation errors
- Missing dependencies

**Debug steps:**

1. **Clean build:**
```bash
cd scripts
./delete-bin-obj-folders.sh
cd ../build
./build.sh
```

2. **Check .NET version:**
```bash
dotnet --version  # Should be 8.0+
```

3. **Restore packages:**
```bash
cd source
dotnet restore MyTelegram.slnx
```

4. **Check for syntax errors:**
```bash
dotnet build MyTelegram.slnx 2>&1 | tee build.log
grep -i error build.log
```

5. **Check Docker build logs:**
```bash
cd build/docker
./1.build-messenger-command-server.sh 2>&1 | tee build.log
```

### Quick Diagnostic Commands

**Check all services status:**
```bash
docker compose ps
```

**Check MongoDB connection:**
```bash
docker compose exec mongodb mongosh tg --eval "db.adminCommand('ping')"
```

**Check RabbitMQ connection:**
```bash
docker compose exec rabbitmq rabbitmqctl status
```

**Check Redis connection:**
```bash
docker compose exec redis redis-cli ping
```

**Tail all logs:**
```bash
docker compose logs -f
```

**Restart specific service:**
```bash
docker compose restart messenger-command-server
```

**Check disk space:**
```bash
df -h
docker system df
```

**Clean Docker cache:**
```bash
docker system prune -a
```

## Configuration Reference

### Complete Environment Variables Table

| Variable | Type | Default | Description | Example |
|----------|------|---------|-------------|---------|
| **Database** |
| `ConnectionStrings__Default` | string | - | MongoDB connection string | `mongodb://mongodb:27017` |
| `App__DatabaseName` | string | `tg` | Main database name | `tg` |
| `App__ReadModelDatabaseName` | string | `tg` | Read model database | `tg` |
| `App__QueryServerEventStoreDatabaseName` | string | `tg` | Query server event store DB | `tg` |
| `App__QueryServerReadModelDatabaseName` | string | `tg` | Query server read model DB | `tg` |
| `App__BotDatabaseName` | string | `tg` | Bot database | `tg` |
| **RabbitMQ** |
| `RabbitMQ__Connections__Default__HostName` | string | - | RabbitMQ host | `rabbitmq` |
| `RabbitMQ__Connections__Default__Port` | int | `5672` | RabbitMQ port | `5672` |
| `RabbitMQ__Connections__Default__UserName` | string | - | RabbitMQ username | `test` |
| `RabbitMQ__Connections__Default__Password` | string | - | RabbitMQ password | `CHANGE_ME` |
| `RabbitMQ__EventBus__ExchangeName` | string | - | Event bus exchange | `MyTelegramExchange` |
| **Redis** |
| `Redis__Configuration` | string | - | Redis connection | `redis:6379` |
| **MinIO** |
| `Minio__Endpoint` | string | - | MinIO endpoint | `minio:9000` |
| `Minio__AccessKey` | string | - | MinIO access key | `test` |
| `Minio__SecretKey` | string | - | MinIO secret key | `CHANGE_ME` |
| `Minio__BucketName` | string | `tg-files` | Bucket name | `tg-files` |
| `Minio__CreateBucketIfNotExists` | bool | `true` | Auto-create bucket | `true` |
| **Security** |
| `App__AccessHashSecretKey` | string | - | Secret for access hash | `CHANGE_ME` |
| `App__EncryptionConfig__Enabled` | bool | `true` | Enable encryption | `true` |
| `App__EncryptionConfig__MessageKeys__0__Id` | int | - | Message key ID | `1` |
| `App__EncryptionConfig__MessageKeys__0__Key` | string | - | Message encryption key | `CHANGE_ME` |
| `App__EncryptionConfig__PhoneKey` | string | - | Phone encryption key | `CHANGE_ME` |
| `App__EncryptionConfig__IndexKeys__0__Id` | int | - | Index key ID | `1` |
| `App__EncryptionConfig__IndexKeys__0__Key` | string | - | Index encryption key | `CHANGE_ME` |
| **WebRTC (REQUIRED for calls)** |
| `App__WebRtcConnections__0__Ip` | string | - | TURN/STUN server IP | `YOUR_SERVER_IP` |
| `App__WebRtcConnections__0__Ipv6` | string | - | IPv6 address (optional) | `::1` |
| `App__WebRtcConnections__0__Port` | int | - | TURN/STUN port | `3478` |
| `App__WebRtcConnections__0__Turn` | bool | - | Enable TURN | `true` |
| `App__WebRtcConnections__0__Stun` | bool | - | Enable STUN | `true` |
| `App__WebRtcConnections__0__UserName` | string | - | TURN username | `testgram` |
| `App__WebRtcConnections__0__Password` | string | - | TURN password | `testgram123` |
| `App__WebRtcConnections__1__*` | - | - | Additional server (optional) | Same as above |
| **Server Configuration** |
| `App__DcOptions__0__Enabled` | bool | `true` | Enable DC | `true` |
| `App__DcOptions__0__IpAddress` | string | - | Server IP | `YOUR_SERVER_IP` |
| `App__DcOptions__0__Port` | int | - | Server port | `20443` |
| `App__DcOptions__0__Id` | int | `1` | DC ID | `1` |
| `App__Servers__0__Enabled` | bool | `true` | Enable server | `true` |
| `App__Servers__0__Port` | int | - | Listen port | `20443` |
| `App__Servers__0__ServerType` | int | `0` | Server type (0=TCP, 1=HTTP) | `0` |
| `App__Servers__0__Ipv6` | bool | `false` | Enable IPv6 | `false` |
| **Authentication** |
| `App__FixedVerifyCode` | string | - | Fixed code for testing (empty in prod) | `12345` |
| `App__VerificationCodeLength` | int | `5` | Code length | `5` |
| `App__VerificationCodeExpirationSeconds` | int | `300` | Code expiration | `300` |
| `App__EnableEmailLogin` | bool | `false` | Enable email login | `false` |
| `App__CheckPhoneNumberFormat` | bool | `true` | Validate phone format | `true` |
| **Features** |
| `App__CreateTestUsers` | bool | `false` | Create test users on startup | `false` |
| `App__EnableSearchNonContacts` | bool | `true` | Search non-contacts | `true` |
| `App__JoinChatDomain` | string | - | Chat invite domain | `https://t.me` |
| `App__PasskeyRpId` | string | - | Passkey relying party ID | `your.domain.com` |
| `App__PasskeyRpName` | string | - | Passkey relying party name | `Testgram` |
| **Bot Configuration** |
| `App__MyTelegramBotOptions__BotApiBaseUrl` | string | - | Bot API URL | `http://bot-server:8080` |
| `App__MyTelegramBotOptions__Token` | string | - | Bot token | - |
| `App__MyTelegramBotOptions__WebHookUrl` | string | - | Webhook URL | `http://localhost:10004/bot/ProcessBotCommand` |
| `App__SubscribeLocalRequest` | bool | `true` | Subscribe local requests | `true` |
| `App__SubscribeRemoteRequest` | bool | `true` | Subscribe remote requests | `true` |
| `App__UseExternalWebHookSender` | bool | `false` | Use external webhook | `false` |
| `App__WebHookSenderUrl` | string | - | Webhook sender URL | - |
| **Data Center** |
| `App__ThisDcId` | int | `1` | This DC ID | `1` |
| `App__MediaDcId` | int | `2` | Media DC ID | `2` |
| `App__MediaOnly` | bool | `false` | Media-only DC | `false` |
| `App__UploadRootPath` | string | `uploads` | Upload directory | `uploads` |
| **File Server** |
| `App__FileServerGrpcServiceUrl` | string | - | File server gRPC URL | `http://file-server:8080` |
| `App__IdGeneratorGrpcServiceUrl` | string | - | ID generator URL | `http://localhost:10002` |
| `App__PrivateKeyFilePath` | string | - | RSA private key path | `private.pkcs8.key` |
| **SMS Providers** |
| `TwilioSms__Enabled` | bool | `false` | Enable Twilio | `false` |
| `TwilioSms__AccountSId` | string | - | Twilio account SID | - |
| `TwilioSms__AuthToken` | string | - | Twilio auth token | - |
| `TwilioSms__FromNumber` | string | - | From phone number | - |
| `TwilioSms__MessagingServiceSId` | string | - | Messaging service SID | - |
| `VonageSms__Enabled` | bool | `false` | Enable Vonage | `false` |
| `VonageSms__BrandName` | string | - | Brand name | - |
| `VonageSms__ApiKey` | string | - | API key | - |
| `VonageSms__ApiSecret` | string | - | API secret | - |
| **Email** |
| `EmailSenderOptions__FromAddress` | string | - | From email | `noreply@testgram.com` |
| `EmailSenderOptions__FromDisplayName` | string | `MyTelegram` | Display name | `Testgram` |
| `EmailSenderOptions__SmtpEmailOptions__Host` | string | - | SMTP host | `smtp.gmail.com` |
| `EmailSenderOptions__SmtpEmailOptions__Port` | int | - | SMTP port | `587` |
| `EmailSenderOptions__SmtpEmailOptions__UserName` | string | - | SMTP username | - |
| `EmailSenderOptions__SmtpEmailOptions__Password` | string | - | SMTP password | - |
| `EmailSenderOptions__SmtpEmailOptions__EnableSsl` | bool | `false` | Enable SSL | `true` |
| **Payments** |
| `App__Stripe__PublishableKey` | string | - | Stripe public key | `pk_test_...` |
| `App__Stripe__SecretKey` | string | - | Stripe secret key | `sk_test_...` |
| **Logging** |
| `Serilog__MinimumLevel__Default` | string | `Information` | Log level | `Information` |
| `Serilog__MinimumLevel__Override__Microsoft` | string | `Warning` | Microsoft log level | `Warning` |
| **Docker** |
| `MyTelegramVersion` | string | `latest` | Image version tag | `latest` |
| `MyTelegramRegistry` | string | `mytelegram` | Docker registry | `mytelegram` |

### Minimal Working Configuration

```bash
# .env file for local development
ConnectionStrings__Default=mongodb://mongodb:27017
App__DatabaseName=tg
App__ReadModelDatabaseName=tg

RabbitMQ__Connections__Default__HostName=rabbitmq
RabbitMQ__Connections__Default__Password=test123
RabbitMQ__EventBus__ExchangeName=MyTelegramExchange

Redis__Configuration=redis:6379

Minio__Endpoint=minio:9000
Minio__AccessKey=test
Minio__SecretKey=test123

App__AccessHashSecretKey=my-secret-key-change-in-prod
App__EncryptionConfig__MessageKeys__0__Key=32-byte-key-change-in-prod-!!!
App__EncryptionConfig__IndexKeys__0__Key=32-byte-key-change-in-prod-!!!

App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=true
App__WebRtcConnections__0__Stun=true
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123

App__DcOptions__0__IpAddress=YOUR_SERVER_IP
App__DcOptions__0__Port=20443

App__FixedVerifyCode=12345
```

## Build & Deploy Quick Reference

### One-Liner Commands

**Quick rebuild single service:**
```bash
cd build/docker && ./1.build-messenger-command-server.sh && cd ../../docker/compose && docker compose up -d messenger-command-server
```

**Rebuild all services:**
```bash
cd build/docker && ./build-all-amd64.sh && cd ../../docker/compose && docker compose up -d
```

**Check compilation errors:**
```bash
cd source && dotnet build MyTelegram.slnx 2>&1 | grep -i error
```

**Tail logs for specific service:**
```bash
docker compose logs -f messenger-command-server
```

**Tail logs for all services:**
```bash
docker compose logs -f
```

**Full restart without rebuild:**
```bash
docker compose down && docker compose up -d
```

**Clean restart with rebuild:**
```bash
docker compose down && cd ../../build/docker && ./build-all-amd64.sh && cd ../../docker/compose && docker compose up -d
```

**Check service health:**
```bash
docker compose ps && docker compose logs --tail=50 messenger-command-server
```

**Quick MongoDB query:**
```bash
docker compose exec mongodb mongosh tg --eval "db.call_sessions.find().limit(5).pretty()"
```

**Check RabbitMQ queues:**
```bash
docker compose exec rabbitmq rabbitmqctl list_queues
```

**Clean build from scratch:**
```bash
cd scripts && ./delete-bin-obj-folders.sh && cd ../build && ./build.sh
```

**Test specific project:**
```bash
cd source && dotnet test test/MyTelegram.Domain.Tests/
```

**Run all tests:**
```bash
cd source && dotnet test MyTelegram.slnx
```

**Check Docker disk usage:**
```bash
docker system df && docker compose ps -a
```

**Clean Docker cache:**
```bash
docker system prune -a -f
```

**Export MongoDB collection:**
```bash
docker compose exec mongodb mongodump --db tg --collection call_sessions --out /tmp/backup
```

**Import MongoDB collection:**
```bash
docker compose exec mongodb mongorestore --db tg --collection call_sessions /tmp/backup/tg/call_sessions.bson
```

### Build Scripts Reference

| Script | Purpose | Output |
|--------|---------|--------|
| `build/build.sh` | Build all projects locally | `out/local/<version>/` |
| `build/docker/1.build-messenger-command-server.sh` | Build command server image | Docker image |
| `build/docker/2.build-messenger-query-server.sh` | Build query server image | Docker image |
| `build/docker/5.build-gateway-server.sh` | Build gateway image | Docker image |
| `build/docker/6.build-auth-server.sh` | Build auth server image | Docker image |
| `build/docker/build-all-amd64.sh` | Build all images (AMD64) | All Docker images |
| `build/docker/build-all-arm64.sh` | Build all images (ARM64) | All Docker images |
| `scripts/delete-bin-obj-folders.sh` | Clean build artifacts | - |
| `scripts/setup_call_indexes.sh` | Create call indexes | MongoDB indexes |
| `scripts/seed_reactions.py` | Import reactions | MongoDB + MinIO |

### Docker Compose Commands

**Start all services:**
```bash
docker compose up -d
```

**Stop all services:**
```bash
docker compose down
```

**Restart specific service:**
```bash
docker compose restart messenger-command-server
```

**View service logs:**
```bash
docker compose logs -f messenger-command-server
```

**Check service status:**
```bash
docker compose ps
```

**Rebuild and restart service:**
```bash
docker compose up -d --build messenger-command-server
```

**Scale service:**
```bash
docker compose up -d --scale messenger-command-server=3
```

**Execute command in container:**
```bash
docker compose exec messenger-command-server bash
```

**View resource usage:**
```bash
docker compose stats
```

## Voice & Video Calls Architecture

### Call Flow

**1. Request Call (Initiator → Server)**
```csharp
// RequestCallHandler.cs
var session = new CallSessionDocument
{
    CallId = Random.Shared.NextInt64(),
    AccessHash = Random.Shared.NextInt64(),
    CallerId = input.UserId,
    CalleeId = calleeId,
    State = "requested",
    GAHash = obj.GAHash,  // Diffie-Hellman hash
    Video = obj.Video,
    Date = currentDate
};
await _callCollection.InsertOneAsync(session);
```

**2. Accept Call (Receiver → Server)**
```csharp
// AcceptCallHandler.cs
var update = Builders<CallSessionDocument>.Update
    .Set(x => x.State, "accepted")
    .Set(x => x.GB, obj.GB);  // Diffie-Hellman B
await _callCollection.UpdateOneAsync(filter, update);
```

**3. Confirm Call (Initiator → Server)**
```csharp
// ConfirmCallHandler.cs
var update = Builders<CallSessionDocument>.Update
    .Set(x => x.State, "confirmed")
    .Set(x => x.KeyFingerprint, obj.KeyFingerprint);
await _callCollection.UpdateOneAsync(filter, update);

// Return WebRTC servers
var connections = new TVector<IPhoneConnection>();
foreach (var config in webRtcConnections)
{
    connections.Add(new TPhoneConnectionWebrtc
    {
        Ip = config.Ip,
        Port = config.Port,
        Turn = config.Turn,
        Stun = config.Stun,
        Username = config.UserName,
        Password = config.Password
    });
}
```

**4. Exchange ICE Candidates**
```csharp
// SendSignalingDataHandler.cs
// Clients exchange ICE candidates via updates
var updatePhoneCallSignalingData = new TUpdatePhoneCallSignalingData
{
    PhoneCallId = callId,
    Data = obj.Data  // ICE candidate
};
```

**5. Discard Call**
```csharp
// DiscardCallHandler.cs
var update = Builders<CallSessionDocument>.Update
    .Set(x => x.State, "discarded")
    .Set(x => x.Duration, duration)
    .Set(x => x.DiscardReason, reason);
await _callCollection.UpdateOneAsync(filter, update);
```

### WebRTC Configuration Pattern

**Reading from IOptions:**
```csharp
public class GetCallConfigHandler : RpcResultObjectHandler<RequestGetCallConfig, TDataJSON>
{
    private readonly MyTelegramMessengerServerOptions _options;
    
    public GetCallConfigHandler(IOptions<MyTelegramMessengerServerOptions> optionsAccessor)
    {
        _options = optionsAccessor.Value;
    }
    
    protected override async Task<TDataJSON> HandleCoreAsync(...)
    {
        if (_options.WebRtcConnections == null || _options.WebRtcConnections.Count == 0)
        {
            throw new InvalidOperationException(
                "WebRTC connections not configured. " +
                "Please configure App__WebRtcConnections in .env file."
            );
        }
        
        // Build config JSON
        var config = new
        {
            stun = _options.WebRtcConnections
                .Where(c => c.Stun)
                .Select(c => $"{c.Ip}:{c.Port}")
                .ToList(),
            turn = _options.WebRtcConnections
                .Where(c => c.Turn)
                .Select(c => new
                {
                    host = c.Ip,
                    port = c.Port,
                    username = c.UserName,
                    password = c.Password
                })
                .ToList()
        };
        
        return new TDataJSON
        {
            Data = JsonSerializer.Serialize(config)
        };
    }
}
```

### Call Session Document Schema

```csharp
public class CallSessionDocument
{
    public long Id { get; set; }              // MongoDB _id
    public long CallId { get; set; }          // Unique call ID
    public long AccessHash { get; set; }      // Access hash for security
    public long CallerId { get; set; }        // Initiator user ID
    public long CalleeId { get; set; }        // Receiver user ID
    public long RandomId { get; set; }        // Client random ID
    public string State { get; set; }         // "requested", "accepted", "confirmed", "discarded"
    public byte[] GAHash { get; set; }        // Diffie-Hellman hash
    public byte[] GA { get; set; }            // Diffie-Hellman A
    public byte[] GB { get; set; }            // Diffie-Hellman B
    public long KeyFingerprint { get; set; }  // Encryption key fingerprint
    public bool Video { get; set; }           // Video call flag
    public int Date { get; set; }             // Creation timestamp
    public int Duration { get; set; }         // Call duration (seconds)
    public string DiscardReason { get; set; } // "missed", "hangup", "busy", "disconnect"
    public string ProtocolJson { get; set; }  // PhoneCallProtocol as JSON
}
```

### MongoDB Indexes (Auto-Created)

```javascript
// docker/compose/init-calls.sh
db.call_sessions.createIndex(
    { "CallId": 1, "AccessHash": 1 },
    { unique: true, name: "idx_callid_accesshash" }
);

db.call_sessions.createIndex(
    { "CallerId": 1, "Date": -1 },
    { name: "idx_callerid_date" }
);

db.call_sessions.createIndex(
    { "CalleeId": 1, "Date": -1 },
    { name: "idx_calleeid_date" }
);

db.call_sessions.createIndex(
    { "State": 1, "Date": -1 },
    { name: "idx_state_date" }
);

db.call_sessions.createIndex(
    { "Date": 1 },
    { expireAfterSeconds: 2592000, name: "idx_date" }  // 30 days TTL
);
```

### Coturn Setup

**Install:**
```bash
sudo apt-get update
sudo apt-get install coturn
```

**Configure `/etc/turnserver.conf`:**
```conf
# Listening port
listening-port=3478

# External IP (your server's public IP)
external-ip=YOUR_SERVER_IP

# Realm
realm=testgram.local

# User credentials
user=testgram:testgram123

# Use long-term credentials
lt-cred-mech

# Fingerprint
fingerprint

# Logging
log-file=/var/log/turnserver.log
verbose

# Relay IP (same as external IP)
relay-ip=YOUR_SERVER_IP

# Allow UDP and TCP
no-tcp-relay
```

**Enable and start:**
```bash
sudo systemctl enable coturn
sudo systemctl start coturn
sudo systemctl status coturn
```

**Configure in `.env`:**
```bash
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123
```

**Test TURN server:**
```bash
# Use trickle ICE test: https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/
# Enter:
# STUN: stun:YOUR_SERVER_IP:3478
# TURN: turn:YOUR_SERVER_IP:3478
# Username: testgram
# Password: testgram123
```

### Firewall Configuration

```bash
# Allow TURN/STUN port
sudo ufw allow 3478/udp
sudo ufw allow 3478/tcp

# Allow relay ports (if using port range)
sudo ufw allow 49152:65535/udp
```

### Troubleshooting Calls

**Check WebRTC config in logs:**
```bash
docker compose logs messenger-command-server | grep -i webrtc
```

**Check call session:**
```bash
docker compose exec mongodb mongosh tg
db.call_sessions.findOne({ CallId: NumberLong("123456789") })
```

**Check Coturn logs:**
```bash
sudo tail -f /var/log/turnserver.log
```

**Test connectivity:**
```bash
# Test STUN
nc -u -v YOUR_SERVER_IP 3478

# Check if Coturn is listening
sudo netstat -tulpn | grep 3478
```

## Stories Architecture

### Story Document Schema

```csharp
public class StoryDocument
{
    public ObjectId Id { get; set; }          // MongoDB _id
    public long OwnerPeerId { get; set; }     // User or channel ID
    public int OwnerPeerType { get; set; }    // 0=User, 2=Channel
    public int StoryId { get; set; }          // Unique story ID per owner
    public long Date { get; set; }            // Creation timestamp (long in DB)
    public long ExpireDate { get; set; }      // Expiration timestamp (long in DB, int in TL)
    public string Caption { get; set; }       // Story caption
    public bool Pinned { get; set; }          // Pinned to profile
    public bool NoForwards { get; set; }      // Disable forwarding
    public bool Deleted { get; set; }         // Soft delete flag (default: false)
    public long RandomId { get; set; }        // Client random ID
    public int Period { get; set; }           // Visibility period (seconds)
    public int ViewsCount { get; set; }       // View counter
    public int ForwardsCount { get; set; }    // Forward counter
    public int ReactionsCount { get; set; }   // Reaction counter
    
    // Media fields
    public int MediaType { get; set; }        // 1=Photo, 2=Video
    public long MediaFileId { get; set; }     // File ID
    public long MediaAccessHash { get; set; } // File access hash
    public int MediaDcId { get; set; }        // DC ID
    public byte[] MediaFileReference { get; set; } // File reference
    public long MediaSize { get; set; }       // File size (for video)
    public string MediaMimeType { get; set; } // MIME type (for video)
    public int VideoWidth { get; set; }       // Video width
    public int VideoHeight { get; set; }      // Video height
    public int VideoDuration { get; set; }    // Video duration (seconds)
    
    // Privacy
    public int PrivacyType { get; set; }      // 0=AllowAll, 1=AllowContacts, 2=DisallowAll
    public List<long> AllowUserIds { get; set; } // Whitelist
    public List<long> DisallowUserIds { get; set; } // Blacklist
}
```

### StoryHelper Methods

**CreatePeer:**
```csharp
public static IPeer CreatePeer(int peerType, long peerId)
{
    return peerType switch
    {
        0 => new TPeerUser { UserId = peerId },
        2 => new TPeerChannel { ChannelId = peerId },
        _ => throw new ArgumentException($"Invalid peer type: {peerType}")
    };
}
```

**ResolvePeer:**
```csharp
public static (long peerId, int peerType) ResolvePeer(IInputPeer peer, long defaultUserId)
{
    return peer switch
    {
        TInputPeerSelf => (defaultUserId, 0),
        TInputPeerUser u => (u.UserId, 0),
        TInputPeerChannel c => (c.ChannelId, 2),
        _ => throw new ArgumentException($"Invalid peer type: {peer.GetType().Name}")
    };
}
```

**ConvertToStoryItem:**
```csharp
public static TStoryItem ConvertToStoryItem(StoryDocument doc, long viewerUserId)
{
    return new TStoryItem
    {
        Id = doc.StoryId,
        Date = (int)doc.Date,
        ExpireDate = (int)doc.ExpireDate,  // Cast long to int for TL
        FromId = CreatePeer(doc.OwnerPeerType, doc.OwnerPeerId),
        Media = ConvertMedia(doc),
        Caption = doc.Caption,
        Pinned = doc.Pinned,
        Noforwards = doc.NoForwards,
        Views = new TStoryViews
        {
            ViewsCount = doc.ViewsCount,
            ForwardsCount = doc.ForwardsCount,
            ReactionsCount = doc.ReactionsCount
        }
    };
}
```

### Privacy Rules Types

| Type | Value | Description |
|------|-------|-------------|
| `AllowAll` | 0 | Everyone can view |
| `AllowContacts` | 1 | Only contacts can view |
| `DisallowAll` | 2 | Nobody can view (private) |
| `AllowUsers` | 3 | Specific users whitelist |
| `DisallowUsers` | 4 | Specific users blacklist |
| `AllowCloseFriends` | 5 | Only close friends |

**Privacy check logic:**
```csharp
private bool CanViewStory(StoryDocument story, long viewerId)
{
    if (story.OwnerPeerId == viewerId) return true;  // Owner can always view
    if (story.Deleted) return false;
    if (story.ExpireDate < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
    
    switch (story.PrivacyType)
    {
        case 0: // AllowAll
            return !story.DisallowUserIds?.Contains(viewerId) ?? true;
        case 1: // AllowContacts
            return IsContact(viewerId, story.OwnerPeerId);
        case 2: // DisallowAll
            return story.AllowUserIds?.Contains(viewerId) ?? false;
        default:
            return false;
    }
}
```

### updateStory + updateStoryID Flow

**CRITICAL:** Always return both updates when sending story.

```csharp
// Step 1: Create updateStoryID (maps randomId to storyId)
var updateStoryId = new TUpdateStoryID
{
    Id = storyId,
    RandomId = obj.RandomId
};

// Step 2: Create updateStory (the actual story)
var updateStory = new TUpdateStory
{
    Peer = StoryHelper.CreatePeer(ownerPeerType, ownerPeerId),
    Story = storyItem
};

// Step 3: Return both in Updates
return new TUpdates
{
    Updates = new TVector<IUpdate> { updateStoryId, updateStory },
    Users = new TVector<IUser>(),
    Chats = new TVector<IChat>(),
    Date = currentDate
};
```

**Why both are needed:**
- `updateStoryID` - Client maps its randomId to server's storyId
- `updateStory` - Client receives the actual story data

### Story Media Handling

**Photo:**
```csharp
if (obj.Media is TInputMediaUploadedPhoto photoMedia)
{
    var savedMedia = await mediaHelper.SaveMediaAsync(obj.Media);
    
    if (savedMedia is TMessageMediaPhoto photoMsg && photoMsg.Photo is TPhoto photo)
    {
        storyDocument.MediaType = 1;
        storyDocument.MediaFileId = photo.Id;
        storyDocument.MediaAccessHash = photo.AccessHash;
        storyDocument.MediaDcId = photo.DcId;
        storyDocument.MediaFileReference = photo.FileReference.ToArray();
    }
}
```

**Video:**
```csharp
if (obj.Media is TInputMediaUploadedDocument docMedia)
{
    var savedMedia = await mediaHelper.SaveMediaAsync(obj.Media);
    
    if (savedMedia is TMessageMediaDocument docMsg && docMsg.Document is TDocument doc)
    {
        storyDocument.MediaType = 2;
        storyDocument.MediaFileId = doc.Id;
        storyDocument.MediaAccessHash = doc.AccessHash;
        storyDocument.MediaDcId = doc.DcId;
        storyDocument.MediaFileReference = doc.FileReference.ToArray();
        storyDocument.MediaSize = doc.Size;
        storyDocument.MediaMimeType = doc.MimeType;
        
        // Extract video attributes
        foreach (var attr in docMedia.Attributes)
        {
            if (attr is TDocumentAttributeVideo videoAttr)
            {
                storyDocument.VideoWidth = videoAttr.W;
                storyDocument.VideoHeight = videoAttr.H;
                storyDocument.VideoDuration = (int)videoAttr.Duration;
            }
        }
    }
}
```

### Story Queries

**Get user's stories:**
```javascript
db.stories.find({
    OwnerPeerId: NumberLong("123456"),
    OwnerPeerType: 0,
    Deleted: false
}).sort({ Date: -1 })
```

**Get pinned stories:**
```javascript
db.stories.find({
    OwnerPeerId: NumberLong("123456"),
    Pinned: true,
    Deleted: false
}).sort({ Date: -1 })
```

**Get expired stories:**
```javascript
var now = Math.floor(Date.now() / 1000);
db.stories.find({
    ExpireDate: { $lt: now },
    Deleted: false
})
```

**Count user's active stories:**
```javascript
var now = Math.floor(Date.now() / 1000);
db.stories.countDocuments({
    OwnerPeerId: NumberLong("123456"),
    Deleted: false,
    ExpireDate: { $gt: now }
})
```

## Business Features

### Business Chat Links

**Collection**: `businesschatlinks`

**Document Structure:**
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

**Handlers:**
- `CreateBusinessChatLinkHandler` - Create link (max 10 per user)
- `EditBusinessChatLinkHandler` - Edit existing link
- `DeleteBusinessChatLinkHandler` - Delete link
- `GetBusinessChatLinksHandler` - List user's links
- `ResolveBusinessChatLinkHandler` - Resolve slug to user

**Limits**: Max 10 links per user (enforced in handler)

**Example:**
```csharp
// Check limit before creating
var existingCount = await collection.CountDocumentsAsync(
    Builders<BsonDocument>.Filter.Eq("UserId", userId)
);
if (existingCount >= 10)
{
    RpcErrors.RpcErrors400.TooMuchBusinessChatLinks.ThrowRpcError();
}
```

### Business Settings

**Stored in**: `eventflow-userreadmodel` (UserFullReadModel)

**Fields:**
- `BusinessWorkHours` - Working hours configuration
- `BusinessLocation` - Business location
- `BusinessGreetingMessage` - Auto-reply for new chats
- `BusinessAwayMessage` - Auto-reply when away
- `BusinessIntro` - Business introduction

**Handlers:**
- `UpdateBusinessWorkHoursHandler`
- `UpdateBusinessLocationHandler`
- `UpdateBusinessGreetingMessageHandler`
- `UpdateBusinessAwayMessageHandler`
- `UpdateBusinessIntroHandler`

**Mapping**: `UserFullMapper.cs` maps read model to TL schema

**Example:**
```csharp
var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
var update = Builders<BsonDocument>.Update.Set("BusinessWorkHours", workHoursJson);
await collection.UpdateOneAsync(filter, update);
```

## Quick Replies

### Collection Structure

**Collection**: `quickreplys`

**Document:**
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

**Handlers:**
- `GetQuickRepliesHandler` - List all shortcuts
- `GetQuickReplyMessagesHandler` - Get messages for shortcut
- `CheckQuickReplyShortcutHandler` - Check name availability
- `EditQuickReplyShortcutHandler` - Rename shortcut
- `DeleteQuickReplyShortcutHandler` - Delete shortcut
- `DeleteQuickReplyMessagesHandler` - Delete specific messages
- `ReorderQuickRepliesHandler` - Change order
- `SendQuickReplyMessagesHandler` - Send from shortcut

**Example:**
```csharp
var collection = _database.GetCollection<BsonDocument>("quickreplys");
var filter = Builders<BsonDocument>.Filter.And(
    Builders<BsonDocument>.Filter.Eq("UserId", userId),
    Builders<BsonDocument>.Filter.Eq("ShortcutId", shortcutId)
);
var quickReply = await collection.Find(filter).FirstOrDefaultAsync();
```

## History TTL (Auto-Delete)

**Purpose**: Automatically delete messages after specified time

**Handlers:**
- `GetDefaultHistoryTTLHandler` - Get default TTL
- `SetDefaultHistoryTTLHandler` - Set default for new chats
- `SetHistoryTTLHandler` - Set TTL for specific chat

**TTL Values:**
- `0` - Disabled
- `86400` - 1 day
- `604800` - 1 week
- `2592000` - 1 month
- Custom values in seconds

**Storage:**
- Default TTL: `eventflow-userreadmodel.DefaultHistoryTTL`
- Chat TTL: `eventflow-dialogreadmodel.HistoryTTL`

**Example:**
```csharp
// Set default TTL for user
var userCollection = _mongoDatabase.GetCollection<UserReadModel>("users");
var filter = Builders<UserReadModel>.Filter.Eq(x => x.UserId, userId);
var update = Builders<UserReadModel>.Update.Set(x => x.DefaultHistoryTTL, ttlSeconds);
await userCollection.UpdateOneAsync(filter, update);

// Set TTL for specific dialog
var dialogCollection = _mongoDatabase.GetCollection<DialogReadModel>("dialogs");
var dialogFilter = Builders<DialogReadModel>.Filter.And(
    Builders<DialogReadModel>.Filter.Eq(x => x.UserId, userId),
    Builders<DialogReadModel>.Filter.Eq(x => x.PeerId, peerId)
);
var dialogUpdate = Builders<DialogReadModel>.Update.Set(x => x.HistoryTTL, ttlSeconds);
await dialogCollection.UpdateOneAsync(dialogFilter, dialogUpdate);
```

## Testing

### Running Tests

```bash
cd /root/testgram/source

# Run all tests
dotnet test MyTelegram.slnx

# Run specific test project
dotnet test test/MyTelegram.Domain.Tests/
dotnet test test/MyTelegram.MTProto.Tests/
dotnet test test/MyTelegram.Schema.Tests/
dotnet test test/MyTelegram.Services.Tests/

# Run with verbose output
dotnet test MyTelegram.slnx --verbosity detailed

# Run specific test
dotnet test --filter "FullyQualifiedName~MyTest"
```

### Test Projects

| Project | Purpose |
|---------|---------|
| `MyTelegram.Domain.Tests` | Domain logic tests |
| `MyTelegram.MTProto.Tests` | MTProto protocol tests |
| `MyTelegram.Schema.Tests` | TL schema tests |
| `MyTelegram.Services.Tests` | Service layer tests |
| `MyTelegram.Domain.IntegrationTests` | Integration tests |

## Troubleshooting

### Calls Not Working

1. **Check WebRTC config:**
```bash
docker compose exec messenger-command-server env | grep WebRtc
```

2. **Check Coturn:**
```bash
sudo systemctl status coturn
sudo tail -f /var/log/turnserver.log
```

3. **Check call sessions:**
```bash
docker compose exec mongodb mongosh tg
db.call_sessions.find().sort({Date: -1}).limit(5)
```

4. **Check indexes:**
```bash
docker compose logs call-init
db.call_sessions.getIndexes()
```

### MongoDB Issues

1. **Check connection:**
```bash
docker compose exec mongodb mongosh tg --eval "db.adminCommand('ping')"
```

2. **Check collections:**
```bash
docker compose exec mongodb mongosh tg --eval "db.getCollectionNames()"
```

3. **Check event store:**
```bash
db.getCollectionNames().filter(c => c.startsWith('eventflow-'))
```

### RabbitMQ Issues

1. **Check queues:**
```bash
docker compose exec rabbitmq rabbitmqctl list_queues
```

2. **Check connections:**
```bash
docker compose exec rabbitmq rabbitmqctl list_connections
```

3. **Management UI**: http://localhost:15672 (guest/guest)

### Build Issues

1. **Clean build:**
```bash
cd /root/testgram/scripts
./delete-bin-obj-folders.sh
cd ../build
./build.sh
```

2. **Check .NET version:**
```bash
dotnet --version  # Should be 8.0+
```

3. **Restore packages:**
```bash
cd /root/testgram/source
dotnet restore MyTelegram.slnx
```

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

**Manual setup:**
```bash
cd /root/testgram/scripts
./setup_call_indexes.sh

# Or via mongosh
docker compose exec mongodb mongosh tg < setup_call_indexes.js
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

**Key metrics:**
- MongoDB connections and query time
- RabbitMQ queue depth
- Redis hit rate
- API response times
- Call success rate
- Error rates

**Logs:**
```bash
# Application logs
docker compose logs -f messenger-command-server
docker compose logs -f messenger-query-server

# Infrastructure logs
docker compose logs -f mongodb
docker compose logs -f rabbitmq
```

### Backup

**MongoDB:**
```bash
docker compose exec mongodb mongodump --db tg --out /backup
```

**MinIO:**
```bash
# Use MinIO client (mc)
mc mirror minio/tg-files /backup/files
```

## Additional Resources

### Official Telegram Documentation

**Core API:**
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

**Android:**
- Repository: https://github.com/DrKLO/Telegram
- Search methods: https://github.com/DrKLO/Telegram/search?q=sendReaction

**iOS:**
- Repository: https://github.com/TelegramMessenger/Telegram-iOS
- Search methods: https://github.com/TelegramMessenger/Telegram-iOS/search?q=sendReaction

**Desktop (TDesktop):**
- Repository: https://github.com/telegramdesktop/tdesktop
- Search methods: https://github.com/telegramdesktop/tdesktop/search?q=sendReaction

**Web (TWeb):**
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

## Getting Help

1. Check this CLAUDE.md for patterns and examples
2. Check logs: `docker compose logs -f <service>`
3. Check MongoDB: `docker compose exec mongodb mongosh tg`
4. Search codebase for similar implementations
5. Check official Telegram documentation
6. Look at official client implementations

## Important Notes

- **DO NOT** modify event store collections directly
- **DO NOT** skip event emission in aggregates
- **DO NOT** use public STUN servers (removed for security)
- **DO NOT** commit sensitive data (passwords, keys)
- **ALWAYS** validate user input
- **ALWAYS** check permissions before operations
- **ALWAYS** use RpcErrors for client errors
- **ALWAYS** test calls after WebRTC changes
- **ALWAYS** check external resources before implementing features
- **ALWAYS** read method documentation at https://core.telegram.org/method/<method_name>
- **ALWAYS** check TL schema at https://core.telegram.org/schema
- **ALWAYS** look at official client implementations for reference

## Quick Reference Summary

### When implementing a new feature:

1. Read https://core.telegram.org/method/<method_name>
2. Read related API pages (stars, gifts, business, etc.)
3. Check TL schema at https://core.telegram.org/schema
4. Search in official clients (Android, iOS, Desktop)
5. Create handler in `Handlers/LatestLayer/<Category>/`
6. Use MongoDB direct access for business features
7. Use RpcErrors for error handling
8. Test with official Telegram client
9. Test with Testgram client

### Common patterns:

- **Handler**: `internal sealed class MyHandler : RpcResultObjectHandler<TRequest, TResponse>`
- **MongoDB**: `_database.GetCollection<BsonDocument>("collection_name")`
- **Error**: `RpcErrors.RpcErrors400.FieldInvalid.ThrowRpcError()`
- **UserId**: Always use `input.UserId` (from token, not from request)
- **TVector**: Always `new TVector<T>()`, never null
- **Date**: `(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()`
- **ExpireDate**: Store as `long` in MongoDB, cast to `int` for TL

### Debug checklist:

- [ ] Check logs: `docker compose logs -f messenger-command-server`
- [ ] Check MongoDB: `db.collection.findOne({ UserId: NumberLong("123") })`
- [ ] Check handler is `internal sealed class`
- [ ] Check namespace is correct
- [ ] Check build errors: `dotnet build MyTelegram.slnx`
- [ ] Check TVector fields are not null
- [ ] Check required TL fields are set
- [ ] Check type casts (long to int)
- [ ] Test with official client

### Build & deploy:

```bash
# Quick rebuild
cd build/docker && ./1.build-messenger-command-server.sh && cd ../../docker/compose && docker compose up -d messenger-command-server

# Full restart
docker compose down && cd ../../build/docker && ./build-all-amd64.sh && cd ../../docker/compose && docker compose up -d

# Check logs
docker compose logs -f messenger-command-server

# Check MongoDB
docker compose exec mongodb mongosh tg
```

---

**End of CLAUDE.md**

This comprehensive guide should help you implement Telegram features correctly and avoid common pitfalls. Always refer to official documentation and client implementations when in doubt.
