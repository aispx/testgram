# Testgram Development Guide

Self-hosted C# Telegram server (fork of MyTelegram). MTProto 2.0, API Layer 222.

**Stack:** .NET 10, CQRS + Event Sourcing (EventFlow), MongoDB, RabbitMQ, Redis, MinIO, Coturn

---

## Project Structure

```
source/src/
├── MyTelegram.Messenger/           # Main business logic
│   ├── Handlers/LatestLayer/       # RPC handlers (ADD NEW HANDLERS HERE)
│   │   ├── Messages/               # Message operations
│   │   ├── Stickers/               # Sticker management
│   │   ├── Channels/               # Channel operations
│   │   ├── Help/                   # Help & promo
│   │   └── ...                     # Other categories
│   ├── Services/                   # Application services
│   └── Converters/                 # Entity converters
├── MyTelegram.Schema/              # TL schema entities (AUTO-GENERATED)
├── MyTelegram.GatewayServer/       # MTProto gateway
├── MyTelegram.QueryHandlers.MongoDB/ # Read model queries
└── MyTelegram.Domain/              # Domain aggregates & events
```

**Key Directories:**
- `build/docker/` - Docker build scripts
- `docker/compose/` - Docker Compose setup
- `docs/` - Setup guides

---

## What Counts as "Not Implemented"

**CRITICAL: A handler is considered NOT IMPLEMENTED if it:**

1. **Throws NotImplementedException**
   ```csharp
   throw new NotImplementedException();
   ```

2. **Returns empty/default response without logic**
   ```csharp
   return new TVector<IUser>();  // Empty list without checking data
   return new TBoolTrue();       // Just returns success without doing anything
   return new TSavedMusic { Count = 0, Documents = [] };  // Empty without checking DB
   ```

3. **Returns null or placeholder data**
   ```csharp
   return null!;
   return new TUpdates { Updates = [], Users = [], Chats = [], Date = CurrentDate };
   ```

**A handler IS properly implemented if it:**
- ✅ Validates input parameters
- ✅ Checks MongoDB/database for actual data
- ✅ Uses proper services (IMessageAppService, IUserAppService, etc.)
- ✅ Returns real data or performs actual operations
- ✅ Handles errors with RpcErrors

**Example of NOT implemented (bad):**
```csharp
protected override Task<ISavedMusic> HandleCoreAsync(IRequestInput input, RequestGetSavedMusic obj)
{
    return Task.FromResult<ISavedMusic>(new TSavedMusic { Count = 0, Documents = [] });
}
```

**Example of properly implemented (good):**
```csharp
protected override async Task<ISavedMusic> HandleCoreAsync(IRequestInput input, RequestGetSavedMusic obj)
{
    // 1. Validate user
    var userReadModel = await _userAppService.GetAsync(input.UserId);
    if (userReadModel == null)
        RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
    
    // 2. Query MongoDB for saved music
    var collection = _database.GetCollection<BsonDocument>("saved_music");
    var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
    var docs = await collection.Find(filter).ToListAsync();
    
    // 3. Build response with real data
    var documents = new TVector<IDocument>();
    foreach (var doc in docs)
    {
        // Convert BsonDocument to IDocument
        documents.Add(ConvertToDocument(doc));
    }
    
    return new TSavedMusic { Count = documents.Count, Documents = documents };
}
```

---

## Implementation Workflow

**CRITICAL: Follow this exact order for ANY feature:**

### 1. Research Phase
```bash
# ALWAYS use TL schema skill to find constructor
/schema.jppgr.am search messages.getStickerSet

# ALWAYS check official Telegram docs
https://core.telegram.org/method/messages.getStickerSet

# ALWAYS search in TDLib (official C++ library) for reference implementation
https://github.com/tdlib/td/tree/master
# TDLib is the official reference implementation - use it for understanding complex features

# ALWAYS search in official Android client for UI/UX reference
https://github.com/DrKLO/Telegram/search?q=getStickerSet

# For web search: Use Google Custom Search API or Yandex Search API
# DO NOT use built-in WebSearch tool - it has limitations
# Google API: https://developers.google.com/custom-search/v1/overview
# Yandex API: https://yandex.com/dev/xml/

# NEVER skip research - it's mandatory!
```

### 2. Implementation Phase

**CRITICAL: Use the right tools and services!**

**Required Services (inject via constructor):**
- `IMongoDatabase` - Direct MongoDB access for queries
- `IUserAppService` - User operations and validation
- `IMessageAppService` - Send messages (including service messages)
- `IPeerHelper` - Convert InputUser/InputPeer to Peer objects
- `IQueryProcessor` - Execute read model queries
- `ILogger<T>` - Logging (optional but recommended)

**Common Patterns:**

1. **Validate User ID:**
```csharp
var userReadModel = await _userAppService.GetAsync(input.UserId);
if (userReadModel == null)
    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
```

2. **Query MongoDB:**
```csharp
var collection = _database.GetCollection<BsonDocument>("collection_name");
var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
var docs = await collection.Find(filter).ToListAsync();
```

3. **Send Service Message:**
```csharp
var action = new TMessageActionSuggestBirthday { Birthday = obj.Birthday };
var sendInput = new SendMessageInput(
    input.ToRequestInfo() with { ReqMsgId = 0 },
    input.UserId,
    new Peer(PeerType.User, targetUserId),
    string.Empty,
    Random.Shared.NextInt64(),
    sendMessageType: SendMessageType.MessageService,
    messageType: MessageType.Text,
    messageAction: action
);
await _messageAppService.SendMessageAsync([sendInput]);
```

**CRITICAL: Service Messages and Updates**

When sending service messages via `SendMessageAsync`, the method works **asynchronously** through event sourcing and does NOT return the created message in the response. This is by design:

- ✅ The message WILL be created and delivered
- ✅ The recipient will receive it via push notification or sync
- ✅ The message will appear in chat history
- ❌ You CANNOT return the message in the immediate Updates response
- ❌ Do NOT try to create fake message objects in Updates

**Correct pattern for service message handlers:**
```csharp
// Send the service message
await messageAppService.SendMessageAsync([sendInput]);

// Return empty Updates (message will arrive via push)
return new TUpdates
{
    Updates = new TVector<IUpdate>(),
    Users = new TVector<IUser>(),
    Chats = new TVector<IChat>(),
    Date = CurrentDate,
    Seq = 0
};
```

**Why this works:**
- Event sourcing processes the message asynchronously
- Push notification system delivers the message to the client
- Client receives the actual message with correct IDs and timestamps
- Trying to return the message immediately would create inconsistencies

**Examples:**
- `SetHistoryTTLHandler` - sends service message, returns empty Updates
- `SuggestBirthdayHandler` - sends service message, returns empty Updates
- `SendMessageHandler` - returns `null!` because messages are async

4. **Validate Access Hash:**
```csharp
var targetPeer = _peerHelper.GetPeer(obj.Id, input.UserId);
```

**Build and Test:**
```bash
# Create handler in correct category
source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetStickerSetHandler.cs

# Follow handler pattern (see below)
# Build and test
cd build/docker && ./1.build-messenger-command-server.sh
```

### 3. Testing Phase
```bash
# Test with official Telegram client (NEVER custom clients first)
# Check logs
docker-compose logs -f messenger-command-server

# Verify MongoDB data
docker-compose exec mongodb mongosh tg
db.eventflow-stickersetreadmodel.findOne()
```

---

## Handler Pattern (Real Example)

```csharp
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get sticker set by ID or short name
/// See https://core.telegram.org/method/messages.getStickerSet
/// </summary>
internal sealed class GetStickerSetHandler : RpcResultObjectHandler<RequestGetStickerSet, IStickerSet>
{
    private readonly IMongoDatabase _database;
    
    public GetStickerSetHandler(IMongoDatabase database)
    {
        _database = database;
    }
    
    protected override async Task<IStickerSet> HandleCoreAsync(
        IRequestInput input, 
        RequestGetStickerSet obj)
    {
        // 1. Validate input
        if (obj.Stickerset is TInputStickerSetShortName shortName)
        {
            if (string.IsNullOrEmpty(shortName.ShortName))
                RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }
        
        // 2. Get userId from token (NEVER from request)
        var userId = input.UserId;
        
        // 3. Query MongoDB
        var collection = _database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("ShortName", shortName.ShortName);
        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        
        if (doc == null)
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        
        // 4. Build response (ALWAYS initialize TVector)
        return new TStickerSet
        {
            Set = new TStickerSet
            {
                Id = doc["StickerSetId"].AsInt64,
                AccessHash = doc["AccessHash"].AsInt64,
                Title = doc["Title"].AsString,
                ShortName = doc["ShortName"].AsString,
                Count = doc["Count"].AsInt32,
                Hash = 0
            },
            Packs = new TVector<IStickerPack>(),      // NEVER null
            Documents = new TVector<IDocument>(),      // NEVER null
            Keywords = new TVector<IStickerKeyword>()  // NEVER null
        };
    }
}
```

**Handler Checklist:**
- ✅ `internal sealed class`
- ✅ Namespace: `MyTelegram.Messenger.Handlers.LatestLayer.<Category>`
- ✅ Use `input.UserId` from token
- ✅ Use `RpcErrors` for errors
- ✅ Initialize all `TVector<T>` (never null)
- ✅ Add XML doc comment with Telegram API link

---

## Real-World Examples from Codebase

### Example 1: GetPromoDataHandler (Help category)
```csharp
// Location: Handlers/LatestLayer/Help/GetPromoDataHandler.cs
protected override async Task<IPromoData> HandleCoreAsync(IRequestInput input, RequestGetPromoData obj)
{
    // Query channel by username
    var collection = _database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
    var filter = Builders<BsonDocument>.Filter.Eq("UserName", "xiegram");
    var channelDoc = await collection.Find(filter).FirstOrDefaultAsync();
    
    if (channelDoc == null)
    {
        // Return empty response (not error)
        return new TPromoDataEmpty { Expires = int.MaxValue };
    }
    
    // Build channel object with required fields
    var channelObj = new TChannel
    {
        Id = channelDoc["ChannelId"].AsInt64,
        AccessHash = channelDoc["AccessHash"].AsInt64,
        Title = channelDoc["Title"].AsString,
        Username = channelDoc["UserName"].AsString,
        // CRITICAL: Add required fields to prevent NullReferenceException
        Photo = new TChatPhotoEmpty(),
        RestrictionReason = new TVector<IRestrictionReason>()
    };
    
    return new TPromoData
    {
        Expires = int.MaxValue,
        Peer = new TPeerChannel { ChannelId = channelDoc["ChannelId"].AsInt64 },
        Chats = new TVector<IChat> { channelObj },
        Users = new TVector<IUser>(),
        PendingSuggestions = new TVector<string>(),
        DismissedSuggestions = new TVector<string>()
    };
}
```

### Example 2: CreateStickerSetHandler (Stickers category)
```csharp
// Location: Handlers/LatestLayer/Stickers/CreateStickerSetHandler.cs
protected override async Task<IStickerSet> HandleCoreAsync(IRequestInput input, RequestCreateStickerSet obj)
{
    // 1. Validate input
    if (string.IsNullOrWhiteSpace(obj.Title))
        RpcErrors.RpcErrors400.PackTitleInvalid.ThrowRpcError();
    
    if (obj.Stickers.Count == 0)
        RpcErrors.RpcErrors400.StickersEmpty.ThrowRpcError();
    
    // 2. Validate ShortName format
    if (!Regex.IsMatch(obj.ShortName, @"^[a-zA-Z][a-zA-Z0-9_]{0,63}$"))
        RpcErrors.RpcErrors400.PackShortNameInvalid.ThrowRpcError();
    
    // 3. Check if ShortName already exists
    var setCol = _database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
    var existingSet = await setCol.Find(
        Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("ShortName", obj.ShortName),
            Builders<BsonDocument>.Filter.Eq("Slug", obj.ShortName)
        )
    ).FirstOrDefaultAsync();
    
    if (existingSet != null)
        RpcErrors.RpcErrors400.PackShortNameOccupied.ThrowRpcError();
    
    // 4. Generate IDs
    var setId = GenerateId();
    var accessHash = GenerateAccessHash();
    
    // 5. Process stickers
    var documentIds = new List<long>();
    var packs = new BsonArray();
    
    foreach (var stickerItem in obj.Stickers)
    {
        if (stickerItem is TInputStickerSetItem sticker)
        {
            documentIds.Add(sticker.Document.Id);
            packs.Add(new BsonDocument
            {
                ["Emoticon"] = sticker.Emoji,
                ["Documents"] = new BsonArray(new[] { (BsonValue)sticker.Document.Id })
            });
        }
    }
    
    // 6. Insert into MongoDB
    await setCol.InsertOneAsync(new BsonDocument
    {
        ["_id"] = $"stickersetreadmodel-{setId}",
        ["StickerSetId"] = setId,
        ["AccessHash"] = accessHash,
        ["ShortName"] = obj.ShortName,
        ["Title"] = obj.Title,
        ["Count"] = documentIds.Count,
        ["DocumentIds"] = new BsonArray(documentIds),
        ["Packs"] = packs
    });
    
    // 7. Return response
    return new TStickerSet { /* ... */ };
}
```

---

## MongoDB Collections Reference

| Collection | Purpose | Key Fields | Common Queries |
|------------|---------|------------|----------------|
| `eventflow-stickersetreadmodel` | Sticker sets | StickerSetId, ShortName, Slug, DocumentIds | Find by ShortName/Slug |
| `eventflow-documentreadmodel` | Documents/files | DocumentId, AccessHash, FileReference | Find by DocumentId |
| `eventflow-channelreadmodel` | Channels | ChannelId, UserName, Title | Find by UserName |
| `eventflow-userreadmodel` | Users | UserId, Phone, Username | Find by UserId |
| `call_sessions` | Voice/video calls | CallId, AccessHash, Date | TTL 30 days |
| `stories` | User/channel stories | OwnerPeerId, StoryId, Date | Find by OwnerPeerId |
| `businesschatlinks` | Business links | UserId, Slug | Find by Slug (unique) |
| `quickreplys` | Quick replies | UserId, ShortcutId | Find by UserId |
| `star-gifts` | Star gifts | GiftId, Stars | Find by GiftId |
| `eventflow-*` | Event sourcing | - | **DO NOT MODIFY DIRECTLY** |

### Common MongoDB Patterns

```csharp
// Query single document
var collection = _database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
var filter = Builders<BsonDocument>.Filter.Eq("ShortName", "mypack");
var doc = await collection.Find(filter).FirstOrDefaultAsync();

// Query with OR condition
var filter = Builders<BsonDocument>.Filter.Or(
    Builders<BsonDocument>.Filter.Eq("ShortName", name),
    Builders<BsonDocument>.Filter.Eq("Slug", name)
);

// Query multiple documents by IDs (batch loading)
var docIds = new List<long> { 123, 456, 789 };
var filter = Builders<BsonDocument>.Filter.In("DocumentId", 
    docIds.Select(id => (BsonValue)new BsonInt64(id)));
var docs = await collection.Find(filter).ToListAsync();

// Insert document
await collection.InsertOneAsync(new BsonDocument
{
    ["_id"] = $"stickersetreadmodel-{id}",
    ["StickerSetId"] = id,
    ["Title"] = "My Pack"
});

// Update document
var update = Builders<BsonDocument>.Update.Set("Title", "New Title");
await collection.UpdateOneAsync(filter, update);

// Safe type conversion for BsonValue
private static long GetInt64(BsonValue v)
{
    return v.BsonType switch
    {
        BsonType.Int64 => v.AsInt64,
        BsonType.Int32 => v.AsInt32,
        BsonType.Double => (long)v.AsDouble,
        _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
    };
}
```

---

## Common Patterns

### Atomic Counter (for generating IDs)
```csharp
var countersCol = _database.GetCollection<BsonDocument>("counters");
var filter = Builders<BsonDocument>.Filter.Eq("_id", "sticker_set_id");
var update = Builders<BsonDocument>.Update.Inc("seq", 1);
var options = new FindOneAndUpdateOptions<BsonDocument> 
{ 
    IsUpsert = true, 
    ReturnDocument = ReturnDocument.After 
};
var result = await countersCol.FindOneAndUpdateAsync(filter, update, options);
var nextId = result["seq"].AsInt64;
```

### Batch Loading (Avoid N+1 Queries)
```csharp
// BAD: N+1 queries
foreach (var id in documentIds)
{
    var doc = await docCol.Find(f => f["DocumentId"] == id).FirstOrDefaultAsync();
}

// GOOD: Single batch query
var filter = Builders<BsonDocument>.Filter.In("DocumentId", documentIds);
var docs = await docCol.Find(filter).ToListAsync();
var docMap = docs.ToDictionary(d => d["DocumentId"].AsInt64);
```

### Safe FileReference Handling
```csharp
// FileReference can be Binary, Array, or null
byte[] fileRef;
if (doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull)
{
    var fr = doc["FileReference"];
    if (fr.BsonType == BsonType.Binary)
        fileRef = fr.AsBsonBinaryData.Bytes;
    else if (fr.BsonType == BsonType.Array)
        fileRef = fr.AsBsonArray.Select(b => (byte)b.AsInt32).ToArray();
    else
        fileRef = [];
}
else
{
    fileRef = [];
}
```

### Reading Config (IOptions Pattern)
```csharp
public MyHandler(IOptions<MyTelegramMessengerServerOptions> options)
{
    var ip = options.Value.WebRtcConnections[0].Ip;
    var port = options.Value.WebRtcConnections[0].Port;
}
```

---

## TL Schema Reference

### Using schema.jppgr.am Skill

```bash
# Search for constructor
/schema.jppgr.am search inputStickerSetItem

# Compare layers
/schema.jppgr.am diff 222 223

# Decode hex payload
/schema.jppgr.am hex2object <hex_string> 222

# Get full layer
/schema.jppgr.am layer 222
```

### Type Mappings

| TL Type | C# Type | Notes |
|---------|---------|-------|
| `int` | `int` | 32-bit signed |
| `long` | `long` | 64-bit signed |
| `string` | `string` | UTF-8 |
| `bytes` | `byte[]` | Binary data |
| `Vector<T>` | `TVector<T>` | **NEVER null** |
| `flags.N?T` | `T?` | Optional field |
| `true` | `bool` | Flag field |

### Common Conversions

```csharp
// Current timestamp
Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()

// ExpireDate (store as long, cast to int for TL)
MongoDB: { "ExpireDate": 1735689600 }  // long
TL Response: ExpireDate = (int)doc["ExpireDate"].AsInt64

// TVector initialization
Packs = new TVector<IStickerPack>()           // Empty
Documents = new TVector<IDocument>(docList)   // With items
```

---

## Top 10 Mistakes (AVOID THESE!)

1. **TVector = null**
   ```csharp
   // ❌ WRONG
   return new TStickerSet { Packs = null };
   
   // ✅ CORRECT
   return new TStickerSet { Packs = new TVector<IStickerPack>() };
   ```

2. **Using request.UserId instead of input.UserId**
   ```csharp
   // ❌ WRONG - client can fake this
   var userId = obj.UserId;
   
   // ✅ CORRECT - from auth token
   var userId = input.UserId;
   ```

3. **Generic exceptions instead of RpcErrors**
   ```csharp
   // ❌ WRONG
   throw new Exception("Invalid sticker set");
   
   // ✅ CORRECT
   RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
   ```

4. **ExpireDate overflow**
   ```csharp
   // ❌ WRONG - int overflow
   ExpireDate = (int)DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds()
   
   // ✅ CORRECT - store as long, cast to int
   MongoDB: { "ExpireDate": 1735689600L }
   TL: ExpireDate = (int)doc["ExpireDate"].AsInt64
   ```

5. **Missing required TL fields**
   ```csharp
   // ❌ WRONG - NullReferenceException
   return new TChannel { Id = 123, Title = "Test" };
   
   // ✅ CORRECT - all required fields
   return new TChannel 
   { 
       Id = 123, 
       Title = "Test",
       Photo = new TChatPhotoEmpty(),
       RestrictionReason = new TVector<IRestrictionReason>()
   };
   ```

6. **Not handling FileReference safely**
   ```csharp
   // ❌ WRONG - can throw
   FileReference = doc["FileReference"].AsBsonBinaryData.Bytes
   
   // ✅ CORRECT - handle all cases
   FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull
       ? doc["FileReference"].AsBsonBinaryData.Bytes
       : []
   ```

7. **Hardcoded IPs/ports**
   ```csharp
   // ❌ WRONG
   var ip = "192.168.1.1";
   
   // ✅ CORRECT
   var ip = _options.Value.WebRtcConnections[0].Ip;
   ```

8. **Modifying eventflow-* collections directly**
   ```csharp
   // ❌ WRONG - breaks event sourcing
   await _database.GetCollection<BsonDocument>("eventflow-useraggregate")
       .UpdateOneAsync(filter, update);
   
   // ✅ CORRECT - use aggregates/events
   // (or use read models like eventflow-userreadmodel)
   ```

9. **N+1 queries**
   ```csharp
   // ❌ WRONG
   foreach (var id in ids)
       var doc = await collection.Find(f => f["Id"] == id).FirstOrDefaultAsync();
   
   // ✅ CORRECT
   var docs = await collection.Find(Builders<BsonDocument>.Filter.In("Id", ids)).ToListAsync();
   ```

10. **Missing Date in TUpdates**
    ```csharp
    // ❌ WRONG
    return new TUpdates { Updates = updates, Users = users, Chats = chats };
    
    // ✅ CORRECT
    return new TUpdates 
    { 
        Updates = updates, 
        Users = users, 
        Chats = chats,
        Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };
    ```

---

## Build & Deploy

### Quick Rebuild Single Service
```bash
cd build/docker && ./1.build-messenger-command-server.sh
cd ../../docker/compose && docker-compose up -d messenger-command-server
```

### Rebuild All Services
```bash
cd build/docker && ./build-all-amd64.sh
cd ../../docker/compose && docker-compose up -d
```

### Check Logs
```bash
docker-compose logs -f messenger-command-server
docker-compose logs -f gateway-server | grep -i error
```

### MongoDB Access
```bash
docker-compose exec mongodb mongosh tg

# Common queries
db.getCollectionNames()
db["eventflow-stickersetreadmodel"].find().limit(5)
db["eventflow-stickersetreadmodel"].findOne({ ShortName: "mypack" })
db["eventflow-documentreadmodel"].find({ DocumentId: NumberLong("123") })
```

### Clean Build
```bash
cd scripts && ./delete-bin-obj-folders.sh
cd ../build && ./build.sh
```

---

## Debugging Guide

### Handler Not Called
```bash
# 1. Check handler class
# Must be: internal sealed class
# Namespace: MyTelegram.Messenger.Handlers.LatestLayer.<Category>

# 2. Rebuild
cd build/docker && ./1.build-messenger-command-server.sh

# 3. Check logs
docker-compose logs -f messenger-command-server | grep -i "handler"
```

### Handler Returns Empty/Wrong Response
```bash
# 1. Check TVector initialization
# All TVector<T> must be initialized (not null)

# 2. Check MongoDB data
docker-compose exec mongodb mongosh tg
db["eventflow-stickersetreadmodel"].findOne({ ShortName: "test" })

# 3. Check logs for exceptions
docker-compose logs -f messenger-command-server | grep -i error
```

### Client Crashes on Response
```bash
# 1. Verify TL schema compatibility
/schema.jppgr.am search inputStickerSetItem

# 2. Check constructor IDs match
# Compare with official clients

# 3. Ensure no null in required fields
# All TVector, Photo, RestrictionReason must be initialized
```

### Calls Not Working
```bash
# 1. Check WebRTC config
docker-compose exec messenger-command-server env | grep WebRtc

# 2. Check Coturn
sudo systemctl status coturn
sudo journalctl -u coturn -f

# 3. Verify call sessions
docker-compose exec mongodb mongosh tg
db.call_sessions.find().sort({Date: -1}).limit(5)
```

---

## WebRTC/Calls Setup

### Coturn Installation
```bash
sudo apt-get install coturn
sudo systemctl enable coturn
```

### Coturn Config (`/etc/turnserver.conf`)
```conf
listening-port=3478
external-ip=YOUR_SERVER_IP
realm=testgram.local
user=testgram:testgram123
lt-cred-mech
fingerprint
log-file=/var/log/turnserver.log
```

### Environment Variables (`.env`)
```bash
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123
```

---

## Critical Rules

### NO STUBS RULE

**CRITICAL: НИКОГДА не делай заглушки без явного запроса пользователя.**

Если функция требует инфраструктуры которой нет (CDN, FileServer, WebProxy) — **пропусти её** или **сообщи что нужна инфраструктура**, но НЕ реализуй пустышку.

**Запрещено без явного "сделай заглушку":**
```csharp
// ❌ ЗАПРЕЩЕНО
return Array.Empty<byte>();        // пустые данные
return new TVector<IFileHash>();   // пустой список без причины
_logger.LogWarning("not implemented"); // и возврат дефолта
throw new NotImplementedException();
```

**Если реализация невозможна — скажи об этом:**
```
GetWebFileHandler требует HTTP proxy инфраструктуры.
GetCdnFileHandler требует CDN DC инфраструктуры.
Эти handlers не могут быть реализованы без дополнительной инфраструктуры.
Реализовать их как заглушки?
```

**Правило:** лучше честный вопрос чем молчаливая пустышка.

---

### Security
- ✅ **ALWAYS** use `input.UserId` from token (never from request)
- ✅ **ALWAYS** validate user input
- ❌ **NEVER** commit sensitive data (.env files)
- ❌ **NEVER** use public STUN servers in production

### Data Integrity
- ❌ **NEVER** modify `eventflow-*` collections directly (use aggregates/events)
- ❌ **NEVER** skip event emission in aggregates
- ✅ **ALWAYS** use RpcErrors for client errors
- ✅ **ALWAYS** initialize TVector fields (never null)

### Development
- ✅ **ALWAYS** check official Telegram docs before implementing
- ✅ **ALWAYS** use schema.jppgr.am skill for TL schema
- ✅ **ALWAYS** test with official Telegram client
- ❌ **NEVER** use NotImplementedException (use RpcErrors)

---

## Quick Reference

### Handler Template
```csharp
namespace MyTelegram.Messenger.Handlers.LatestLayer.<Category>;

internal sealed class MyHandler : RpcResultObjectHandler<TRequest, TResponse>
{
    private readonly IMongoDatabase _database;
    
    public MyHandler(IMongoDatabase database) => _database = database;
    
    protected override async Task<TResponse> HandleCoreAsync(IRequestInput input, TRequest obj)
    {
        // 1. Validate
        // 2. Get userId from input.UserId
        // 3. Query MongoDB
        // 4. Return response (initialize all TVector)
    }
}
```

### Common Code Snippets
```csharp
// MongoDB query
var collection = _database.GetCollection<BsonDocument>("collection_name");
var filter = Builders<BsonDocument>.Filter.Eq("Field", value);
var doc = await collection.Find(filter).FirstOrDefaultAsync();

// Error handling
RpcErrors.RpcErrors400.FieldInvalid.ThrowRpcError();

// TVector initialization
new TVector<T>()                    // Empty
new TVector<T>(list)                // With items
new TVector<T> { item1, item2 }     // Collection initializer

// Current timestamp
(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()

// Safe BsonValue conversion
private static long GetInt64(BsonValue v) => v.BsonType switch
{
    BsonType.Int64 => v.AsInt64,
    BsonType.Int32 => v.AsInt32,
    _ => throw new InvalidCastException()
};
```

---

## Resources

- **Official API:** https://core.telegram.org/api
- **TL Schema:** https://core.telegram.org/schema
- **Methods:** https://core.telegram.org/methods
- **Android Client:** https://github.com/DrKLO/Telegram
- **TDesktop:** https://github.com/telegramdesktop/tdesktop
- **Schema API:** https://schema.jppgr.am

---

## Skills & Tools

### Available Skills

#### schema-jppgr-am
Search Telegram TL schema, compare layers, decode hex payloads, and find constructor IDs.

**Usage:**
```bash
# Search for constructor
/schema-jppgr-am search inputStickerSetItem

# Compare layers
/schema-jppgr-am diff 222 223

# Get layer schema
/schema-jppgr-am layer 222

# Decode hex payload
/schema-jppgr-am hex2object <hex_string> 222
```

**Auto-triggers:** "TL schema", "telegram constructor", "layer diff", "MTProto schema", "constructor ID"

**Common scenarios:**
- Finding constructor IDs: `/schema-jppgr-am search messages.getStickerSet`
- Checking API changes: `/schema-jppgr-am diff 222 223`
- Debugging serialization: `/schema-jppgr-am hex2object <hex> 222`

---

#### check-handler
Verify handler implementation follows best practices and catches common mistakes.

**Usage:**
```bash
/check-handler source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetStickerSetHandler.cs
```

**Checks:**
- ✅ Class declaration (`internal sealed class`)
- ✅ Security (`input.UserId` usage)
- ✅ Error handling (`RpcErrors` usage)
- ✅ TL types (`TVector` initialization)
- ✅ MongoDB patterns (no N+1 queries)

**Use after:** Implementing or modifying handlers

---

#### test-handler
Test a handler by checking logs and MongoDB data.

**Usage:**
```bash
/test-handler GetStickerSetHandler
```

**What it does:**
1. Checks if handler is registered
2. Shows recent logs
3. Queries MongoDB collections
4. Searches for errors
5. Verifies handler file exists

**Use after:** Rebuilding services to verify handler works

---

#### rebuild-service
Rebuild and restart a specific Testgram service.

**Usage:**
```bash
/rebuild-service messenger-command-server
/rebuild-service gateway-server
```

**What it does:**
1. Builds Docker image
2. Restarts container
3. Shows startup logs

**Use after:** Making code changes that need deployment

---

## Claude Code Configuration

This project uses Claude Code with the following setup:

### Permissions (`.claude/settings.local.json`)
```json
{
  "permissions": {
    "allow": [
      "Bash(*)",
      "Read(**)",
      "Edit(**)",
      "Bash(docker compose:*)",
      "Bash(docker-compose:*)"
    ]
  }
}
```

### Recommended Workflow
1. Use `/schema.jppgr.am` skill for TL schema lookups
2. Read handler examples before implementing new features
3. Test with official Telegram client
4. Check MongoDB data after operations
5. Review logs for errors

---

**Last Updated:** 2026-04-03

---

## Fragment API

### Overview
Fragment API позволяет получать информацию о коллекционных username и phone номерах, купленных на Fragment.com.

### Username Architecture

**CRITICAL: Multiple Usernames Support**

Users and channels can have multiple usernames:
- **Basic username** (Editable=true): Regular username, always active, cannot be deactivated
- **Fragment NFT username** (Editable=false): Purchased on Fragment.com, can be activated/deactivated

**Data Structure:**

```csharp
// ReadModel: UsernameInfo class
public class UsernameInfo
{
    public string Username { get; set; }
    public bool Editable { get; set; }  // true = basic, false = Fragment NFT
    public bool Active { get; set; }    // true = active, false = inactive
}

// IUserReadModel / IChannelReadModel
List<UsernameInfo>? Usernames { get; }  // Full username objects

// TL Schema: TUsername
public sealed class TUsername : IUsername
{
    public bool Editable { get; set; }  // Flag bit 0
    public bool Active { get; set; }    // Flag bit 1
    public string Username { get; set; }
}
```

**MongoDB Storage:**

```javascript
// eventflow-userreadmodel
{
  UserId: Long("2010001"),
  UserName: "glebxdlol",           // Primary username (legacy)
  Usernames: [                     // Full username objects
    { Username: "glebxdlol", Editable: true, Active: true },
    { Username: "blockchain", Editable: false, Active: true }
  ]
}
```

**Mapper: UserMapper.cs / ChannelMapper.cs**

```csharp
// Convert Usernames to TVector<IUsername>
if (source.Usernames != null && source.Usernames.Count > 0)
{
    destination.Usernames = new TVector<IUsername>();
    foreach (var usernameInfo in source.Usernames)
    {
        destination.Usernames.Add(new TUsername
        {
            Username = usernameInfo.Username,
            Editable = usernameInfo.Editable,
            Active = usernameInfo.Active
        });
    }
    
    // Set primary username (first active editable, or first active)
    var primary = source.Usernames.FirstOrDefault(u => u.Active && u.Editable)
                  ?? source.Usernames.FirstOrDefault(u => u.Active);
    if (primary != null)
    {
        destination.Username = primary.Username;
    }
}
else
{
    // Fallback to legacy UserName field
    destination.Username = source.UserName;
}
```

**Client Display:**

When client receives `user.usernames: [TL_username]`:
- Shows all active usernames in profile
- NFT usernames (Editable=false) display with Fragment icon
- Clicking NFT username opens `FragmentUsernameBottomSheet` with purchase info

### MongoDB Collection: `fragment_collectibles`

```javascript
{
  _id: "fragment-username-testgram",
  type: "username",              // "username" или "phone"
  username: "testgram",          // для type="username"
  phone: "888123456",            // для type="phone"
  purchase_date: 1704067200,     // Unix timestamp
  currency: "USD",               // ISO 4217 код валюты
  amount: 14500,                 // Цена в минимальных единицах (145.00 USD)
  crypto_currency: "TON",        // Название криптовалюты
  crypto_amount: 50000000000,    // Цена в минимальных единицах TON
  url: "https://fragment.com/username/testgram"
}
```

### Handler: GetCollectibleInfoHandler

**Location:** `source/src/MyTelegram.Messenger/Handlers/LatestLayer/Fragment/GetCollectibleInfoHandler.cs`

**Request:** `fragment.getCollectibleInfo`
- `collectible`: `InputCollectible` (username или phone)

**Response:** `fragment.CollectibleInfo`
- `purchase_date`: дата покупки (unixtime)
- `currency`: валюта (USD, EUR, etc)
- `amount`: цена в минимальных единицах
- `crypto_currency`: криптовалюта (TON)
- `crypto_amount`: цена в минимальных единицах криптовалюты
- `url`: ссылка на Fragment.com

**Errors:**
- `COLLECTIBLE_INVALID` (400): неверный формат collectible
- `COLLECTIBLE_NOT_FOUND` (400): collectible не найден

### Как добавить Fragment collectible

```bash
# Username
docker-compose exec -T mongodb mongosh tg --quiet --eval "
db.fragment_collectibles.insertOne({
  _id: 'fragment-username-myusername',
  type: 'username',
  username: 'myusername',
  purchase_date: $(date +%s),
  currency: 'USD',
  amount: 14500,
  crypto_currency: 'TON',
  crypto_amount: 50000000000,
  url: 'https://fragment.com/username/myusername'
});
"

# Phone (должен начинаться с 888)
docker-compose exec -T mongodb mongosh tg --quiet --eval "
db.fragment_collectibles.insertOne({
  _id: 'fragment-phone-888999888',
  type: 'phone',
  phone: '888999888',
  purchase_date: $(date +%s),
  currency: 'USD',
  amount: 29900,
  crypto_currency: 'TON',
  crypto_amount: 100000000000,
  url: 'https://fragment.com/number/888999888'
});
"
```

### Как это работает в клиенте

1. **ProfileActivity.java** (строка 7120, 7160, 7205):
   - При клике на username/phone проверяется `!usernameObj.editable` или `phone.startsWith("888")`
   - Отправляется `fragment.getCollectibleInfo`
   - Открывается `FragmentUsernameBottomSheet`

2. **FragmentUsernameBottomSheet.java**:
   - `TYPE_USERNAME = 0`: для username
   - `TYPE_PHONE = 1`: для phone номеров
   - Показывает информацию о покупке (дата, цена в TON и USD)
   - Кнопка "View on Fragment" открывает `info.url`

### Примеры использования

```csharp
// Handler автоматически определяет тип collectible
if (obj.Collectible is TInputCollectibleUsername usernameInput)
{
    // Поиск по username
    var filter = Builders<BsonDocument>.Filter.Eq("username", usernameInput.Username.ToLower());
}
else if (obj.Collectible is TInputCollectiblePhone phoneInput)
{
    // Поиск по phone
    var filter = Builders<BsonDocument>.Filter.Eq("phone", phoneInput.Phone);
}
```

### Username Management Handlers

**Implemented handlers for managing multiple usernames:**

1. **account.toggleUsername** - Activate/deactivate user's Fragment username
   - Location: `Handlers/LatestLayer/Account/ToggleUsernameHandler.cs`
   - Cannot deactivate basic username (Editable=true)
   - Max 10 active usernames limit

2. **account.reorderUsernames** - Reorder user's active usernames
   - Location: `Handlers/LatestLayer/Account/ReorderUsernamesHandler.cs`
   - Only active usernames can be reordered
   - First username becomes primary

3. **channels.toggleUsername** - Activate/deactivate channel's Fragment username
   - Location: `Handlers/LatestLayer/Channels/ToggleUsernameHandler.cs`

4. **channels.reorderUsernames** - Reorder channel's active usernames
   - Location: `Handlers/LatestLayer/Channels/ReorderUsernamesHandler.cs`

5. **channels.deactivateAllUsernames** - Deactivate all Fragment usernames for channel
   - Location: `Handlers/LatestLayer/Channels/DeactivateAllUsernamesHandler.cs`
   - Keeps basic usernames (Editable=true) active

6. **bots.toggleUsername** - Activate/deactivate bot's Fragment username
   - Location: `Handlers/LatestLayer/Bots/ToggleUsernameHandler.cs`

7. **bots.reorderUsernames** - Reorder bot's active usernames
   - Location: `Handlers/LatestLayer/Bots/ReorderUsernamesHandler.cs`

### Как назначить NFT username пользователю

```bash
# 1. Добавить Fragment collectible
docker-compose exec -T mongodb mongosh tg --quiet --eval "
db.fragment_collectibles.insertOne({
  _id: 'fragment-username-blockchain',
  type: 'username',
  username: 'blockchain',
  purchase_date: $(date +%s),
  currency: 'USD',
  amount: 14500,
  crypto_currency: 'TON',
  crypto_amount: NumberLong('50000000000'),
  url: 'https://fragment.com/username/blockchain'
});
"

# 2. Обновить Usernames пользователя
docker-compose exec -T mongodb mongosh tg --quiet --eval '
db["eventflow-userreadmodel"].updateOne(
  { UserId: NumberLong("2010001") },
  { 
    $set: { 
      Usernames: [
        { Username: "glebxdlol", Editable: true, Active: true },
        { Username: "blockchain", Editable: false, Active: true }
      ]
    }
  }
)'

# 3. Перезапустить серверы
docker-compose restart messenger-command-server messenger-query-server

# 4. В клиенте: убить процесс, очистить кэш, войти заново
```

### Важные моменты

1. **Username**: хранится в lowercase для поиска в fragment_collectibles
2. **Phone**: должен начинаться с 888 для Fragment номеров
3. **Amount**: в минимальных единицах (145.00 USD = 14500) - хранится как Int32
4. **CryptoAmount**: в минимальных единицах TON (50 TON = 50000000000) - хранится как Int64 (NumberLong)
5. **URL**: должен вести на Fragment.com
6. **Usernames**: ВСЕГДА включает основной username (Editable=true) первым
7. **Client cache**: После изменения username нужно очистить кэш клиента
8. **Primary username**: Устанавливается автоматически в mapper (первый active editable, или первый active)

