# Testgram Development Guide

Self-hosted C# Telegram server (fork of MyTelegram). MTProto 2.0, API Layer 222.

**Stack:** .NET 10, CQRS + Event Sourcing (EventFlow), MongoDB, RabbitMQ, Redis, MinIO, Coturn
**Solution:** `source/MyTelegram.slnx` · **Tests:** `source/test/*`

---

## Critical Rules

### NO STUBS
**NEVER write stubs unless explicitly asked to.** If a feature needs missing infrastructure
(CDN, FileServer, WebProxy) — **say so and ask**, do not return a dummy:

```csharp
// ❌ FORBIDDEN without an explicit "make a stub"
return Array.Empty<byte>();
return new TVector<IFileHash>();          // empty list for no reason
throw new NotImplementedException();
_logger.LogWarning("not implemented");    // + returning a default
```

An honest question beats a silent dummy.

### Security
- **ALWAYS** take the userId from the token: `input.UserId` — never from the request (`obj.UserId` is client-forgeable)
- **ALWAYS** validate input and the access hash
- **NEVER** commit `.env` / secrets; **NEVER** use public STUN servers in production

### Data integrity
- **NEVER** write to `eventflow-*` collections directly — only through aggregates/events
- **NEVER** skip emitting an event in an aggregate
- **ALWAYS** use `RpcErrors...ThrowRpcError()` instead of `throw new Exception`
- **ALWAYS** initialize `TVector<T>` — null crashes the client

---

## Project Structure

```
source/src/
├── MyTelegram.Messenger/              # Business logic
│   ├── Handlers/LatestLayer/<Category>/  # RPC handlers (NEW ONES GO HERE)
│   ├── Services/                      # Application services
│   └── Converters/                    # Entity → TL mappers
├── MyTelegram.Schema/                 # TL entities (AUTO-GENERATED, do not edit)
├── MyTelegram.Domain/                 # Aggregates and events
├── MyTelegram.GatewayServer/          # MTProto gateway
└── MyTelegram.QueryHandlers.MongoDB/  # Read-model queries

build/docker/    # build scripts      docker/compose/  # compose stack      docs/  # guides
```

---

## Implementation Workflow

### 1. Research (mandatory, do not skip)
- `/schema-jppgr-am search <method>` — constructor ID and signature
- https://core.telegram.org/method/<method> — official documentation
- https://github.com/tdlib/td — reference implementation for complex features
- https://github.com/DrKLO/Telegram — client behaviour/UX
- Web search: Google Custom Search API or Yandex XML API (the built-in WebSearch is limited)

### 2. Implementation
Services via the constructor: `IMongoDatabase`, `IUserAppService`, `IMessageAppService`,
`IPeerHelper`, `IQueryProcessor`, `ILogger<T>`.

### 3. Verify
`dotnet test` → rebuild the image → test with the **official** client (not a custom one) →
check the logs and the data in MongoDB.

---

## Handler Pattern

```csharp
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get sticker set by ID or short name.
/// See https://core.telegram.org/method/messages.getStickerSet
/// </summary>
internal sealed class GetStickerSetHandler(
    IMongoDatabase database,
    ILogger<GetStickerSetHandler> logger)
    : RpcResultObjectHandler<Schema.Messages.RequestGetStickerSet, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(
        IRequestInput input, Schema.Messages.RequestGetStickerSet obj)
    {
        // 1. Validate the input
        if (obj.Stickerset is not TInputStickerSetShortName { ShortName.Length: > 0 } shortName)
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();

        // 2. userId — from the token only
        var userId = input.UserId;

        // 3. Query the data
        var collection = database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var doc = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("ShortName", shortName.ShortName))
            .FirstOrDefaultAsync();

        if (doc == null)
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();

        // 4. Response — every TVector initialized
        return new TStickerSet
        {
            Set = new Schema.TStickerSet
            {
                Id = doc["StickerSetId"].AsInt64,
                AccessHash = doc["AccessHash"].AsInt64,
                Title = doc["Title"].AsString,
                ShortName = doc["ShortName"].AsString,
                Count = doc["Count"].AsInt32,
                Hash = 0
            },
            Packs = new TVector<IStickerPack>(),
            Documents = new TVector<IDocument>(),
            Keywords = new TVector<IStickerKeyword>()
        };
    }
}
```

**Checklist:** `internal sealed class` · namespace `...Handlers.LatestLayer.<Category>` ·
`input.UserId` · `RpcErrors` · every `TVector` initialized · XML doc with a link to the API.
Verify with: `/check-handler <path>`.

### What counts as "not implemented"

A handler is NOT implemented if it throws `NotImplementedException`, returns `null!`,
or returns an empty/default response without ever looking at the data:

```csharp
// ❌ not implemented
return Task.FromResult<ISavedMusic>(new TSavedMusic { Count = 0, Documents = [] });
```

It IS implemented if it validates the input, reads real data, uses the services,
performs the operation, and reports errors through `RpcErrors`.

---

## Common Patterns

### User validation
```csharp
var user = await userAppService.GetAsync(input.UserId);
if (user == null)
    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);   // access hash check
```

### Service message + Updates
`SendMessageAsync` works **asynchronously** through event sourcing and does not return the
created message. The message will be created and delivered by push — but it cannot be returned
in the response, and fabricating a message object inside `Updates` is not allowed.

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
    messageAction: action);

await messageAppService.SendMessageAsync([sendInput]);

return new TUpdates
{
    Updates = new TVector<IUpdate>(),
    Users = new TVector<IUser>(),
    Chats = new TVector<IChat>(),
    Date = CurrentDate,     // Date is required
    Seq = 0
};
```
Examples: `SetHistoryTTLHandler`, `SuggestBirthdayHandler`; `SendMessageHandler` returns `null!`.

### Atomic ID counter
```csharp
var result = await database.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
    Builders<BsonDocument>.Filter.Eq("_id", "sticker_set_id"),
    Builders<BsonDocument>.Update.Inc("seq", 1),
    new FindOneAndUpdateOptions<BsonDocument>
    {
        IsUpsert = true,
        ReturnDocument = ReturnDocument.After
    });
var nextId = result["seq"].AsInt64;
```

### Batch instead of N+1
```csharp
// ❌ N+1
foreach (var id in ids)
    await col.Find(f => f["DocumentId"] == id).FirstOrDefaultAsync();

// ✅ a single query
var docs = await col.Find(Builders<BsonDocument>.Filter.In("DocumentId", ids)).ToListAsync();
var map = docs.ToDictionary(d => d["DocumentId"].AsInt64);
```

### Safe Bson reads
```csharp
private static long GetInt64(BsonValue v) => v.BsonType switch
{
    BsonType.Int64  => v.AsInt64,
    BsonType.Int32  => v.AsInt32,
    BsonType.Double => (long)v.AsDouble,
    _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
};

// FileReference may be Binary, Array, or absent
byte[] fileRef = doc.GetValue("FileReference", BsonNull.Value) switch
{
    { BsonType: BsonType.Binary } b => b.AsBsonBinaryData.Bytes,
    { BsonType: BsonType.Array } a  => a.AsBsonArray.Select(x => (byte)x.AsInt32).ToArray(),
    _ => []
};
```

### Config through IOptions (no hardcoded IPs/ports)
```csharp
public MyHandler(IOptions<MyTelegramMessengerServerOptions> options)
{
    var ip = options.Value.WebRtcConnections[0].Ip;
}
```

---

## TL Schema

| TL | C# | Notes |
|----|-----|---------|
| `int` / `long` | `int` / `long` | |
| `string` | `string` | UTF-8 |
| `bytes` | `byte[]` | |
| `Vector<T>` | `TVector<T>` | **never null** |
| `flags.N?T` | `T?` | optional field |
| `true` | `bool` | flag field |

```bash
/schema-jppgr-am search inputStickerSetItem     # find a constructor
/schema-jppgr-am diff 222 223                   # what changed between layers
/schema-jppgr-am layer 222                      # the full layer
/schema-jppgr-am hex2object <hex> 222           # decode a payload
```

**Dates and int overflow:** store timestamps in Mongo as `long`, cast in TL:
`ExpireDate = (int)doc["ExpireDate"].AsInt64`. Current time —
`(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()`.

**Required fields:** on `TChannel`/`TUser` and friends, set `Photo = new TChatPhotoEmpty()`,
`RestrictionReason = new TVector<IRestrictionReason>()` — otherwise the client hits a
NullReferenceException.

---

## MongoDB

| Collection | Purpose | Key fields |
|-----------|------------|-----------|
| `eventflow-stickersetreadmodel` | Sticker sets | StickerSetId, ShortName, Slug, DocumentIds |
| `eventflow-documentreadmodel` | Files | DocumentId, AccessHash, FileReference |
| `eventflow-channelreadmodel` | Channels | ChannelId, UserName, Title |
| `eventflow-userreadmodel` | Users | UserId, Phone, UserName, Usernames |
| `call_sessions` | Calls | CallId, AccessHash, Date (TTL 30 days) |
| `stories` | Stories | OwnerPeerId, StoryId, Date |
| `businesschatlinks` | Business links | UserId, Slug (unique) |
| `quickreplys` | Quick replies | UserId, ShortcutId |
| `star-gifts` | Star gifts | GiftId, Stars |
| `fragment_collectibles` | Fragment NFT | type, username/phone |
| `eventflow-*` | Event sourcing | **do not modify directly** |

```bash
docker compose -p mytelegram exec mongodb mongosh tg

db.getCollectionNames()
db["eventflow-stickersetreadmodel"].findOne({ ShortName: "mypack" })
db["eventflow-documentreadmodel"].find({ DocumentId: NumberLong("123") })
```

---

## Build & Deploy

> Only `docker compose` v2 is installed here (not `docker-compose`), and the stack is named
> **mytelegram** — always pass `-p mytelegram`, otherwise a duplicate `compose-*` stack comes up
> whose mongodb crash-loops on `DBPathInUse`.

```bash
# Tests
dotnet test source/MyTelegram.slnx

# A single service
cd build/docker && ./1.build-messenger-command-server.sh
docker compose -p mytelegram up -d messenger-command-server

# Everything
cd build/docker && ./build-all-amd64.sh
docker compose -p mytelegram up -d

# Logs
docker compose -p mytelegram logs -f messenger-command-server
docker compose -p mytelegram logs -f gateway-server | grep -i error

# Clean rebuild (the disk is nearly full — run docker builder prune -af first)
cd scripts && ./delete-bin-obj-folders.sh && cd ../build && ./build.sh
```

Scripts: `1.` command-server, `2.` query-server, `4.` sms-sender, `5.` gateway-server,
`6.` auth-server, `7.` data-seeder.

---

## Debugging

| Symptom | What to check |
|---------|---------------|
| Handler is not invoked | `internal sealed class`, namespace `...LatestLayer.<Category>`, was the image rebuilt |
| Empty/wrong response | uninitialized `TVector`, the data in Mongo, `logs \| grep -i error` |
| Client crashes on the response | constructor ID match (`/schema-jppgr-am`), unset required fields |
| Calls do not work | `env \| grep WebRtc`, `systemctl status coturn`, `db.call_sessions.find().sort({Date:-1}).limit(5)` |

---

## WebRTC / Calls

`/etc/turnserver.conf`:
```conf
listening-port=3478
external-ip=YOUR_SERVER_IP
realm=testgram.local
user=testgram:testgram123
lt-cred-mech
fingerprint
log-file=/var/log/turnserver.log
```

`.env`:
```bash
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123
```

---

## Fragment / multiple usernames

Users and channels may have several usernames:
- **Basic** (`Editable=true`) — the regular one, always active, cannot be disabled
- **Fragment NFT** (`Editable=false`) — bought on Fragment.com, can be toggled on/off

```csharp
public class UsernameInfo   // read model
{
    public string Username { get; set; }
    public bool Editable { get; set; }   // true = basic, false = Fragment NFT
    public bool Active { get; set; }
}
// TL: TUsername { Editable (flag 0), Active (flag 1), Username }
```

`UserMapper` / `ChannelMapper` convert `Usernames` into a `TVector<IUsername>` and set the
primary `Username` to the first active+editable one, otherwise the first active one; when the
list is empty they fall back to the legacy `UserName` field. The primary username always comes first.

**Handlers:** `account.toggleUsername` / `reorderUsernames` (limit of 10 active, the basic one
cannot be disabled), `channels.toggleUsername` / `reorderUsernames` / `deactivateAllUsernames`,
`bots.toggleUsername` / `reorderUsernames`.

### fragment.getCollectibleInfo

`Handlers/LatestLayer/Fragment/GetCollectibleInfoHandler.cs` — looks up `fragment_collectibles`
by `TInputCollectibleUsername` (username in lowercase) or `TInputCollectiblePhone`.
Errors: `COLLECTIBLE_INVALID`, `COLLECTIBLE_NOT_FOUND`.

```javascript
{
  _id: "fragment-username-testgram",
  type: "username",            // "username" | "phone" (phone starts with 888)
  username: "testgram",
  purchase_date: 1704067200,   // unixtime
  currency: "USD",
  amount: 14500,               // Int32, minor units: 145.00 USD
  crypto_currency: "TON",
  crypto_amount: NumberLong("50000000000"),  // Int64, minor units of TON
  url: "https://fragment.com/username/testgram"
}
```

The client (`ProfileActivity` → `FragmentUsernameBottomSheet`) calls this method when a username
with `!editable` is clicked, or a phone number starting with `888`, and shows the purchase date
and price.

### Granting an NFT username manually
```bash
docker compose -p mytelegram exec -T mongodb mongosh tg --quiet --eval '
db["eventflow-userreadmodel"].updateOne(
  { UserId: NumberLong("2010001") },
  { $set: { Usernames: [
      { Username: "glebxdlol",  Editable: true,  Active: true },
      { Username: "blockchain", Editable: false, Active: true }
  ] } })'

docker compose -p mytelegram restart messenger-command-server messenger-query-server
# In the client: kill the process, clear the cache, log in again
```

---

## Skills

| Skill | Purpose |
|-------|---------|
| `/schema-jppgr-am` | TL schema: find constructors, diff layers, decode hex |
| `/check-handler <path>` | Check a handler for common mistakes |
| `/test-handler <Name>` | Registration, logs, data in Mongo |
| `/rebuild-service <svc>` | Build the image + restart + startup logs |

---

## Resources

- API: https://core.telegram.org/api · Methods: https://core.telegram.org/methods · Schema: https://core.telegram.org/schema
- TDLib: https://github.com/tdlib/td · Android: https://github.com/DrKLO/Telegram · TDesktop: https://github.com/telegramdesktop/tdesktop
- Schema API: https://schema.jppgr.am
