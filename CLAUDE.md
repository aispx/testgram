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

### Throwaway scripts go in a temp directory, not in `scripts/`
`scripts/` is for tooling the project keeps: seeders and the checked-in verification scripts. A probe
written to check one change, a one-off query, a scratch Python file — write it to a temp directory
(`$TMPDIR`, `/tmp`, or the agent's own job temp dir) and **do not commit it**. Committing it leaves a
script nobody maintains next to the ones that are actually run, and the next person cannot tell them
apart.

If a probe turns out to be worth keeping — it covers a whole surface, it is idempotent, its usage is
documented here — say so and ask before adding it to `scripts/`.

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

**Anything the client renders is verified against the official Telegram service, not against the
logs.** "Prod" here always means **real Telegram** (`api.telegram.org`, DC `149.154.167.51`) — never
this deployment. Call every method the surface uses on this server *and* on real Telegram with
`hash = 0`, diff the counts, then the contents; follow each returned id through
`getCustomEmojiDocuments`/`getStickerSet` to a real `upload.getFile`; re-quote every hash and check it
comes back `*NotModified` and is never 0. A method answering successfully is not evidence the client
can draw it — an empty list and a zero hash look identical to healthy in every log. Probing recipes,
including the authorized Telethon session for each side: "Emoji categories and animated emoji" below.

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
| `channel_admin_log` | Admin log (recent actions) | channel_id, event_id (per-channel counter), filters[], search_text, date (TTL 48 h) |
| `scheduled_messages` | Schedule queue | ScheduledMessageId, PeerId/PeerType, SenderUserId, ScheduleDate (`0x7FFFFFFE` = when online), RepeatPeriod, Item (full `MessageItem`), ClaimedUntil |
| `account_deletions` | Delayed account deletion (2FA) | `_id`/UserId, PhoneNumber, Hash (confirmphone link), DeleteAt, RequestedByPermAuthKeyId, PhoneCodeHash, ClaimedUntil |
| `user_status` | Last seen presence | UserId, LastOnline, Online |
| `passport_values` | Telegram Passport documents | `_id` = `{UserId}:{Type}`, Data/DataHash/DataSecret, *FileId(s), Hash |
| `passport_files` | Passport scans (descriptors) | Id, AccessHash, OwnerUserId, FileHash, Secret, Size, Parts |
| `passport_file_parts` | Passport scans (bodies) | `_id` = `{FileId}_{PartIndex}`, FileId, Offset, Bytes |
| `passport_errors` | Errors reported by a bot | UserId, BotId, Kind, Type, Hash/Hashes, Field, Text |
| `passport_phones` / `passport_emails` | Verified plain values | UserId, Phone / Email |
| `bot-verifier-settings` | Bots allowed to verify (seeded by hand) | BotId (unique), Icon, Company, CustomDescription, CanModifyCustomDescription |
| `bot-verifications` | Issued verification badges | BotId, Icon, Company, Description, UserId **or** ChannelId |
| `saved_gifs` | Saved GIFs (`messages.getSavedGifs`) | `_id` = `{UserId}:{DocumentId}`, UserId, DocumentId, Order (desc = newest first), Date |
| `gif_mp4_conversions` | `image/gif` → MPEG4 twins | `_id` = source DocumentId, Mp4DocumentId, Date |
| `tenor_gifs` | Tenor GIFs imported on send | `_id` = Tenor id, DocumentId, Date |
| `web_file_cache` | Bodies proxied for `upload.getWebFile` | `_id` = SHA-256 of the URL, Url, MimeType, Bytes, CachedAt (TTL `App__WebFiles__CacheSeconds`) |
| `web_file_registrations` | URLs already registered with the file-server | `_id` = SHA-256 of the URL, Url, MimeType, FileId, Date |
| `emoji_groups` | Emoji/sticker/GIF search categories | For (`default`/`stickers`/`status`/`profile_photo`), Kind (`default`/`greeting`/`premium`), Title, IconEmojiId, Emoticons, Order |
| `emoji_keywords` | Localized emoji keyword lists (`messages.getEmojiKeywords`) | `_id` = `emoji-keyword-{lang}-{keyword}`, LangCode, Keyword, Emoticons, Version (one per language), Deleted |
| `default_emoji_lists` | Curated custom-emoji pickers (`account.getDefault*Emojis`) | `_id`/For (`profile_photo`/`group_photo`/`background`), DocumentIds (ordered) |
| `installed_sticker_sets` | Sticker sets a user installed | `_id` = `{UserId}:{StickerSetId}`, StickerSetType (`Regular`/`Mask`/`CustomEmoji`), Archived, Order (desc = top of panel), Date (`installed_date`) |
| `featured_sticker_sets` / `featured_emoji_sticker_sets` | Trending sets | StickerSetId, Order (asc), Archived (= dropped out of trending, served by `getOldFeaturedStickers`) |
| `read_featured_sticker_sets` | Trending badge, per user | `_id` = `{UserId}:{StickerSetType}`, ReadSetIds |
| `faved_stickers` / `recent_stickers` | Favourites and recents | UserId, DocumentId, Order (desc), Date; `recent_stickers` also Attached (mask list) |
| `attached_stickers` | Sets baked into a photo/video | `_id` = `photo:{id}` or `document:{id}`, StickerSetIds |
| `chatlist_invites` | Exported chat-folder links | `_id` = `chatlist-invite-{slug}`, Slug, CreatorUserId, FilterId, Title, PeerIds/PeerTypes, Revoked |
| `chatlist_hidden_updates` | Peers dismissed in a shared folder | `_id` = `{UserId}:{FilterId}`, HiddenPeerIds |
| `top_peers_settings` | `contacts.toggleTopPeers` opt-out | `_id` = UserId, Disabled |
| `top_peers_excluded` | Peers reset out of the rating | `_id` = `{UserId}-{Category\|all}-{PeerType}-{PeerId}`, UserId, Category (absent = every category), PeerType, PeerId |
| `top_peer_usage` | Recorded uses behind the categories no message expresses | UserId, Category, PeerType, PeerId, Date (unix), CreatedAt (TTL 90 days) |
| `eventflow-*` | Event sourcing | **do not modify directly** |

```bash
docker compose -p mytelegram exec mongodb mongosh tg

db.getCollectionNames()
db["eventflow-stickersetreadmodel"].findOne({ ShortName: "mypack" })
db["eventflow-documentreadmodel"].find({ DocumentId: NumberLong("123") })
```

---

## Files and server-side video processing

File bodies live in the MinIO bucket `tg-files` under the plain file id (`{fileId}`, thumbnails as
`{fileId}_{sizeType}`). A body uploaded by a client is AES-256-CTR encrypted, and its key/iv sit in
`eventflow-filereadmodel`; a body created by the server (stickers, video renditions) is stored as-is
and has no entry there. Document metadata belongs to the **file-server** (external image, native
build, no source) and is created through its gRPC `MediaService` — `App__FileServerGrpcServiceUrl`.

```
gRPC SaveFile(id, accessHash, data, thumbSize)   # writes a body, max ~4 MB per call
gRPC CreateDocument(...)                          # registers the document read model
```

Because `SaveFile` caps at 4 MB, larger bodies (video renditions) are written straight to MinIO with
an AWS SigV4 PUT (`StoredFileStorage`, credentials from `Minio__AccessKey`/`Minio__SecretKey`), and
only the metadata goes through `CreateDocument`.

`messages.sendMedia` with a video addressed to a broadcast channel of at least
`App__VideoProcessing__MinChannelParticipants` members parks the message in the schedule queue with
`video_processing_pending`, converts it with **ffmpeg** (installed in the messenger-command-server
image) into `App__VideoProcessing__Heights` renditions, attaches them as
`messageMediaDocument.alt_documents` and only then sends it — see
https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing .
Conversion runs in `VideoProcessingBackgroundService`; after 3 failed attempts the video is delivered
unconverted rather than lost.

---

## GIFs

A GIF on Telegram is an **MPEG4 without sound** — `video/mp4` plus `documentAttributeAnimated`. Both
halves matter: tdlib refuses to save anything else ("Only MPEG4 animations can be saved") and tdesktop
drops non-`isGifv()` documents out of the list it receives. Everything lives in `Services/Gifs/`.

* **Order is the contract.** Clients adopt the server's vector verbatim and hash it *in order*, so the
  list must be newest-saved-first and a re-save must move the entry to the front. `SavedGifStore` does
  that with a per-user counter (`counters/saved_gifs_order_{userId}`) and sorts `Order` descending.
* **The hash is unsigned.** `SavedGifHashHelper` implements
  [hash generation](https://corefork.telegram.org/api/offsets#hash-generation) over `document.id` with a
  `ulong` accumulator. `MessageSearchMongoHelper.CalcHash` shifts a signed `long` and therefore
  disagrees with every client once the accumulator goes negative — do not reuse it here.
  Android hashes only the first 200 ids, so `getSavedGifs` accepts that prefix variant too.
* **The server evicts**, not the client: past `saved_gifs_limit_default` (200) /
  `saved_gifs_limit_premium` (400) the oldest entry is deleted. Returning more than the limit breaks the
  hash, because clients truncate before hashing.
* **Sending a GIF saves it.** `SentGifProcessor`, hooked into `sendMedia` and `sendMultiMedia`, converts
  and then adds it — clients add to their *local* list on send without calling `saveGif`, so without this
  the entry vanishes on the next refetch.
* **`image/gif` is converted on send** by `GifTranscodeService` (ffmpeg `-an`, h264, yuv420p) and
  published by `GifDocumentPublisher`. The mapping is kept in `gif_mp4_conversions` so `saveGif` on the
  original id still works.
* **A server-made GIF document is written by hand**, because neither upload route produces one. gRPC
  `SaveMedia` with `inputMediaUploadedDocument` merges the parts of an upload the *file-server itself*
  received through `upload.saveFilePart` (it keeps them in its own upload directory), so parts staged in
  Mongo are invisible to it and it answers `messageMediaEmpty`. The gRPC `CreateDocument` shortcut
  hardcodes sticker attributes — a document created through it comes back with
  `documentAttributeImageSize(512, 512)` and `documentAttributeSticker`, never
  `documentAttributeAnimated`, which every client's saved-GIF list requires. Both were measured against
  the running file-server. So `GifDocumentPublisher` stores the body with `IStoredFileStorage` (plain
  SigV4 PUT under the file id, as the video renditions do) and writes the `eventflow-documentreadmodel`
  row itself, with `documentAttributeAnimated` + `documentAttributeVideo(nosound)` + the filename. Same
  sanctioned exception as the web file row below: `DocumentAggregate` in this repo is a stub and the real
  one lives in the closed file-server.
* **`@gif` is a real system bot** (`MyTelegramConsts.GifSearchBotUserId`), seeded by `UserDataSeeder`
  with `InlineEnabled` in `botfather-bot-state`, and answered **in-process** by `GifSearchBotService`
  (`getInlineBotResults` short-circuits on its id, like `SendMessageHandler` does for BotFather — the bot
  has no MTProto session). Results are this server's own GIFs plus Tenor
  (`App__Gifs__Tenor__*`); Tenor hits are `webDocumentNoProxy` URLs and are only imported as documents
  when the user picks one (`TenorGifImporter`, cached in `tenor_gifs`).
* **The grid tile plays `thumb`, not `content`.** Android swaps to the thumb for a `gif` result exactly
  when its mime type is `video/mp4` (`ContextLinkCell`), and tdlib treats such a thumbnail as an
  animation, so the preview is Tenor's `nanomp4` (~25 KB) with the full `mp4` only referenced. Tenor's
  animated `tinygif` is *not* requested at all: at 0.3–1.4 MB per result it is larger than the MPEG4 it
  previews, and thirty of them is what made GIF search look broken. Tenor answers are cached in Redis for
  `App__Gifs__CacheTimeSeconds`, because clients re-query on every keystroke.
* **Inline web media must be a *proxied* `webDocument`.** Android tests `instanceof TL_webDocument` and
  `TL_webDocumentNoProxy` is a sibling class, not a subclass, so a no-proxy result draws an empty tile
  forever — a GIF search answered with `webDocumentNoProxy` renders nothing at all. `InlineResultConverter`
  therefore emits `webDocument` with an access hash that is an HMAC of the URL (`WebDocumentUrlSigner`).
* **`upload.getWebFile` is answered by the file-server, not by this repo.** Its handler derives a file id
  from the URL, looks it up with `GetWebFileByFileIdQuery` and answers `WEBDOCUMENT_INVALID` when there is
  no row. Its own gRPC `SaveWebFile` (added to `mediaservice.proto`; field numbers were read out of the
  binary) does download and store the body, but creating the row fails inside that image — *"Reflection-based
  serialization has been disabled for this application"* in `WebFileDownloader.DownloadAsync`, because it is
  a native-AOT build whose `WebFile` aggregate lost the reflection EventFlow needs. So `WebFileRegistrar`
  does the half the binary cannot: gRPC `SaveWebFile` for the body, then it writes the
  `eventflow-webfilereadmodel` row itself. That is the one sanctioned exception to "never write
  `eventflow-*` directly" — the owning aggregate is closed source and its write path is broken, so a
  proxied `webDocument` is otherwise unreadable. A URL that fails to register goes out as
  `webDocumentNoProxy` instead, and only registered URLs are proxied (`IWebDocumentProxy.CanProxy`).
  The messenger keeps its own `GetWebFileHandler` for a deployment without that file-server.

See https://corefork.telegram.org/api/gifs

---

## Stickers

Everything lives in `Services/Stickers/`. The catalogue of sets is `eventflow-stickersetreadmodel`,
filled by `scripts/seed_stickers.py`; everything per-user is a plain collection (see the table above).

* **A hash a client sends is a hash the client computed.** For those, the server has to reproduce the
  client's algorithm, not invent one — `VectorHashHelper` is the shared
  [unsigned accumulator](https://corefork.telegram.org/api/offsets#hash-generation)
  (`MediaDataController.calcHash`, tdlib `get_vector_hash`). What goes into it differs per method and
  is not guessable: `allStickers` folds in **each set's own `stickerSet.hash`**
  (`calcStickersHash`, tdlib `get_sticker_sets_hash`), `featuredStickers` folds in **`set.id` plus an
  extra `1` per unread set** (`calcFeaturedStickersHash`, `get_featured_sticker_sets_hash`), and
  `recentStickers`/`favedStickers`/`stickers`/`foundStickers` fold in **document ids**. Get the input
  wrong and `*NotModified` can never fire; the tell is a method called far more often than its refresh
  interval.
* **`stickerSet.hash` is the server's to define, but only from the catalogue row.**
  `StickerSetHashHelper` hashes id, short name, count, `Version` and the ordered
  `(documentId, pack emoji)` pairs — deliberately nothing from the document rows, because
  `getAllStickers` cannot afford to load every document of every installed set on each poll, and the
  number it reports has to equal the one `getStickerSet` reports. A client caches whichever copy it saw
  last and then hashes its panel from that. Any edit that the ids and pack emoji do not express — a
  title, a thumbnail, a re-seed that only fixes per-document alts — must bump `Version`, which is why
  the seeder `$inc`s it instead of writing `1`.
* **A `stickers.*` response replaces the client's copy of the set.** They all return the full
  `messages.stickerSet` through `IStickerSetMapper.BuildFullAsync`; answering with empty
  `documents`/`packs`, as they used to, empties the pack in the client
  (Android `MediaDataController.putStickerSet`).
* **The per-sticker methods find the set by document.** `changeSticker`, `changeStickerPosition`,
  `removeStickerFromSet` and `replaceSticker` receive only an `InputDocument`;
  `IStickerSetStore.FindByDocumentIdAsync` is the lookup. They previously filtered
  `StickerSetId == documentId` and so answered `STICKERSET_INVALID` for every possible input.
* **`stickerPack` is the set's emoji index, grouped by emoji.** One pack per sticker — even with the
  right emoji — means only one sticker is ever found by it, because clients build their own
  emoji-to-sticker map straight from the vector. `StickerSetEditor.Rebuild` keeps `DocumentIds`,
  `Packs`, `Keywords` and `Count` consistent by construction.
* **Limits are enforced by the server, and the number must match what the client was told**
  (`stickers_faved_limit_*`, `config.stickers_recent_limit`): clients truncate a list to the limit
  *before* hashing it, so returning one entry too many breaks caching permanently. Past the limit the
  oldest entry is dropped; past the installed-set ceiling (appConfig `stickers_installed_limit`,
  default 200 — Telegram does not publish this one) the oldest sets are archived and the install answers
  `messages.stickerSetInstallResultArchive`.
* **Re-installing an archived set is how clients un-archive it** — there is no separate method.
* **`getArchivedStickers` is usually asked for the count alone**: Android calls it with `limit = 0`
  purely to number the "Archived stickers" row (`loadArchivedStickersCount`) and hides the row when the
  count is zero.
* **`config.preload_featured_stickers` has to stay off.** It asks clients to load the full sets behind the
  trending lists up front, and Android does the custom-emoji half with
  `loadStickers(TYPE_FEATURED_EMOJIPACKS)` — a constant of `6` indexing `MediaDataController.stickerSets`
  and `stickersByIds`, which are built with **six** slots (0..5). So the flag plus a non-empty
  `getFeaturedEmojiStickers` is `ArrayIndexOutOfBoundsException: length=6; index=6` on the UI thread a
  second after launch: a crash loop, not a degraded screen. The arrays are the same size upstream and real
  Telegram serves the flag unset (measured), so there is no version of this where enabling it helps.
* **`readFeaturedStickers` with an empty `id` vector means "clear the whole badge"** — Android sends it
  that way from `markFeaturedStickersAsRead` and only fills a single id when one set was opened. The
  request carries no masks/emojis flag, so the bare form clears both taxonomies.
* **Every list mutation pushes an update** (`IStickerUpdateNotifier`): without it a second device shows
  the old favourites, order and panel until its own hourly refresh. The `update_stickersets_order` flag
  on the send methods is the same story in reverse — `SentStickerProcessor` moves the set to the top and
  emits `updateMoveStickerSetToTop`, because the panel is re-read from `getAllStickers` and a client that
  reordered only locally loses it.
* **A nested TL `Id` is stored as `_id`.** The driver's automapper maps a member named `Id` to the
  `_id` element, including inside a subdocument, so `documentAttributeSticker.stickerset` is written as
  `{"_t": "TInputStickerSetID", "_id": …}`. The seeders wrote `"Id"`, which parses without error and
  yields `inputStickerSetID(id = 0)` — invisible inside a stickerset response, where the field is
  overwritten anyway, but in the flat lists it is a sticker whose pack cannot be opened.
  `StickerAttributeSerializer` accepts both shapes so no migration is needed; new writes use `_id`.
* **`messages.getAttachedStickers` needs the send path.** The sets come from
  `inputMediaUploadedPhoto.stickers` / `inputMediaUploadedDocument.stickers`, which nothing read before:
  `AttachedStickerRecorder` records them from `sendMedia`, `sendMultiMedia` and `uploadMedia`, and marks
  the media with `photo.has_stickers` / `documentAttributeHasStickers` — that flag is what makes a client
  offer the action at all.

```bash
# one-off: move installed sets out of the eventflow read model
MONGO_URL=mongodb://172.23.0.8:27017 python3 scripts/migrate_installed_sticker_sets.py --dry-run
MONGO_URL=mongodb://172.23.0.8:27017 python3 scripts/migrate_installed_sticker_sets.py

# set flags (official/masks/emojis/thumb) and the trending list, read from real Telegram.
# Do NOT use seed_stickers.py --import for this on a live deployment: it rebuilds emoji_groups,
# emoji_keywords and featured_emoji_sticker_sets from its own manifest and would wipe the emoji taxonomy.
TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder MONGO_URL=... \
  python3 scripts/seed_sticker_set_flags.py --download
MONGO_URL=... python3 scripts/seed_sticker_set_flags.py --import   # --dry-run supported

# end-to-end check: every list is read twice, the second time quoting the hash the server returned,
# so a missing *NotModified shows up as a failure. Needs the server's RSA public key (see the header).
TG_API_ID=... TG_API_HASH=... TG_SERVER_PUBKEY=server_pub.pem TG_PHONE=... MONGO_URL=... \
  python3 scripts/verify_stickers.py
```

See https://corefork.telegram.org/api/stickers

---

## Animated dice

`inputMediaDice` carries only an emoji; the **server** mints the outcome. One table owns the whole
surface — `Services/Dice/DiceEmojiHelper.cs`: emoji → set short name, highest value, winning
value/frame. Sending, `inputStickerSetDice` resolution and the `emojies_send_dice` /
`emojies_send_dice_success` config fields all read it, and a test asserts the table still matches
what `AppConfigHelper` actually emits (the config keeps its own generated copy because
`AppConfigHelper._hash` is a constant: change the emitted bytes without bumping it and clients sit on
`appConfigNotModified` forever).

* **An emoji outside the table is `EMOTICON_INVALID`.** Any string used to be accepted and rolled
  1..6, producing a `messageMediaDice` whose sticker set no client can resolve — a permanently blank
  bubble that logs nothing. The range is the server's job too: tdlib's `MessageDice::is_valid()`
  allows up to 6 for 🎲/🎯 and up to **1000** for the rest, then draws nothing past the end of the set.
  Values start at 1, because 0 is the client's "not rolled yet" sentinel.
* **A dice set has to satisfy two incompatible lookups at once.** tdlib indexes `documents`
  positionally (`sticker_ids_[value]`, index 0 = idle preview), tdesktop ignores the order and reads
  the keycap packs (`#⃣` → 0, `1⃣`..`6⃣` → 1..6). The seeded sets do both; a re-seed that reorders
  documents or drops the numeric packs breaks one family silently. `DiceStickerSetManifestTests`
  pins it. The slot machine is the exception: 21 documents, positional for everyone, its 1..64 value
  decoded as a bitfield into background/lever/three reels.
* **The right is `send_stickers`, not `send_games`** — tdlib refuses a dice under `can_send_stickers`
  ("Not enough rights to send dice to the chat") and Android gates on `canSendStickers`.
* **Nowhere but a plain send.** No edit, no album, no draft (`MEDIA_INVALID` each — tdlib marks the
  content non-editable, not groupable, not local), and no caption (dropped, as Android does itself).
  A forward keeps the value; sending the same emoji again re-rolls.
* **`messages.uploadMedia` is answered by the external file-server**, which returns a rolled dice and
  cannot be changed from here. The guard in this repo's `UploadMediaHandler` only covers a deployment
  without that image — same situation as `GetWebFileHandler`.
* **Revoke waits 24 hours in private chats** (`403 MESSAGE_DELETE_FORBIDDEN`), so a roll cannot be
  taken back. Saved Messages, groups and channels are exempt, matching
  `MediaDice::allowsRevoke`/`MessagesManager.cpp`. The check runs *before* `ClearMentionsAsync`,
  which is not reversible.
* **TON stake dice is not implemented.** `getEmojiGameInfo` answers `emojiGameUnavailable` — the
  honest answer, and exactly what clients gate the staking UI on — and `inputMediaStakeDice` is
  `MEDIA_INVALID` instead of silently becoming a message with no media.

Probing it against a running deployment needs a throwaway Telethon script (temp dir, not `scripts/`);
copy the connection and login block out of `scripts/verify_stickers.py`, which already registers this
server's RSA key. What is worth asserting, all of it verified once by hand: each of the six sets read
twice so a missing `stickerSetNotModified` on the re-quote shows up; `documents[i]` equal to the
document named by the `i` keycap pack; a roll per emoji inside its range; the caption coming back
empty; `EMOTICON_INVALID`, `EMOTICON_STICKERPACK_MISSING` and the four `MEDIA_INVALID` refusals; a
forward keeping the value; and `MESSAGE_DELETE_FORBIDDEN` on revoking a fresh private-chat dice.
Telethon humanizes error strings, so match on the error **class** name
(`MEDIA_INVALID` → `MediaInvalidError`).

See https://corefork.telegram.org/api/dice

---

## Animated emojis

Three separate surfaces, all served from here: the animated emoji itself, the sound it plays when
clicked, and the reaction animation the *other* participant sees.

* **The two special sets are mirrored whole and must stay identical to prod**: `AnimatedEmojies`
  (`inputStickerSetAnimatedEmoji`) is 599 documents / 580 packs, `EmojiAnimations`
  (`inputStickerSetAnimatedEmojiAnimations`) 121 / 124, `EmojiGenericAnimations` 6 / 1 — measured
  against the live service. `StickerSetStore.SpecialShortNames` maps the parameterless
  `InputStickerSet` constructors onto those short names.
* **`EmojiAnimations` carries a keycap pack (`1⃣`) listing every document, and that is not junk.**
  tdlib picks which animation to play by looking for a keycap-number emoji among a sticker's *pack*
  emoticons (`get_emoji_number`, exactly `'0'..'9'` + U+20E3) and finds the candidates by comparing
  the *document's* `alt` to the clicked emoji (`get_animated_emoji_click_stickers`). So the `alt` has
  to be the bare emoji and the numeric packs have to survive every re-seed: the `i` index a client
  sends in the interaction payload is that keycap number, and dropping the pack makes every click a
  no-op.
* **`emojies_sounds` is per session and cannot live in `AppConfigHelper.g.cs`.** The map hands out an
  `access_hash` that clients quote back in `upload.getFile`, and document access hashes here are
  minted from the caller's `AccessHashKeyId` (`AccessHashHelper2`), which the closed file-server
  validates. So `GetAppConfigHandler` appends the key per caller through `IEmojiSoundAppConfigBuilder`
  and folds its hash into the config hash — otherwise a client that re-logs in is answered
  `appConfigNotModified` while holding access hashes that download nothing. Real Telegram can publish
  one constant per document; this server cannot.
* **All three fields of a sound are `jsonString`.** tdlib's `ConfigManager` skips any member of the
  object that is not a string and then rejects the entry for the missing id; Android
  (`MessagesController.applyAppConfig`) casts to `TL_jsonString` and decodes the reference with
  `Base64.URL_SAFE`. A numeric `id` is silently discarded by every client. The reference is unpadded
  base64url (`is_base64url` rejects some padded lengths); Telegram currently ships it empty, we ship
  the document's real one.
* **Nine sounds, copied verbatim** (🎃 ⚰ 🧟 🧟‍♂ 🧟‍♀ 🍑 🎊 🎄 🦾, ~4–7 KB Ogg each), stored as plain
  `audio/ogg` documents that belong to no sticker set — a client never fetches the `document` object
  for a soundbite, tdlib registers the id as a bare `FullRemoteFileLocation(FileType::VoiceNote, …)`.
  An emoji whose document row is missing is dropped rather than advertised.
* **`sendMessageEmojiInteraction.msg_id` must be translated, because private chats number messages per
  user.** The clicking client names the message by *its own* id, the receiving client resolves it in
  *its own* box, so relaying the action verbatim delivers a healthy-looking `updateUserTyping` that
  points at an unrelated message — nothing is drawn and nothing is logged anywhere.
  `IEmojiInteractionMsgIdMapper` (in `SetTypingHandler`, private chats only) reuses
  `GetReplyToMsgIdListQuery`, the same mapper `sendMessage` uses for reply ids, and drops the update
  instead of failing the call when the message cannot be mapped. Verified: A's `32002` arrives at B as
  B's own `52`, and back. Group/channel peers stay a plain passthrough — one fan-out update cannot
  carry a different id per member, and clients only send interactions in private chats.

```bash
# 1. download from real Telegram (9 sounds), 2. import bodies + rows (--dry-run supported)
TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \
  python3 scripts/seed_emoji_sounds.py --download
MONGO_URL=mongodb://172.23.0.8:27017 MINIO_ENDPOINT=172.23.0.10:9000 \
MINIO_ACCESS_KEY=... MINIO_SECRET_KEY=... \
  python3 scripts/seed_emoji_sounds.py --import
```

See https://corefork.telegram.org/api/animated-emojis

---

## Emoji categories and animated emoji

The tabs above the GIF/sticker/emoji grid are **animated custom-emoji documents**, not system emoji.
Android's `EmojiView.updateGifTabs()` looks each entry of `appConfig.gif_search_emojies` up with
`MediaDataController.getEmojiAnimatedSticker`, which searches only the `AnimatedEmojies` pack loaded via
`messages.getStickerSet(inputStickerSetAnimatedEmoji)`; a miss falls back to the static system glyph,
which is what "эмодзи как дефолт" looks like. The category bar inside the search field is the same idea
through `messages.getEmojiGroups` → `emojiGroup.icon_emoji_id` → `messages.getCustomEmojiDocuments`.

* **Icons all come from one dedicated set, `EmojiCategories`** (`emojis = true`, `text_color = true`, 36
  documents, all `free = false`), matching official Telegram — measured against the live service. Borrowing
  icons from whatever custom-emoji set happens to be seeded gives semantically wrong tiles (a clown for
  "Smileys & People"). `text_color` is what makes clients recolour them to the theme.
* **The search taxonomy is not the keyboard taxonomy.** `getEmojiGroups` serves Love, Approval,
  Disapproval, Cheers, Laughter, Astonishment, Sadness, Anger, Neutral, Doubt, Silly — not
  "Smileys & People / Animals & Nature / …". Each of the four methods serves a different list, and
  `emojiGroupPremium` appears only in the sticker taxonomy. `scripts/seed_emoji_categories.py` copies all
  four verbatim; `EmojiGroupsAppService` sorts on `Order`, which preserves the served order.
* **A category whose icon document is missing must be dropped, not shipped**: TDLib discards it
  (`EmojiGroupList::get_emoji_categories_object`), so iOS/Desktop lose the category while Android draws a
  blank tile.
* **`alt` belongs to the document, not to the pack.** Telegram's `stickerPack.emoticon` carries no U+FE0F
  while the documents' `alt` does (207 of 852 seeded documents). Deriving `alt` from the pack strips it;
  Android copes because it strips FE0F on both sides, tdlib-based clients compare raw and miss.
  `GetStickerSetHandler.PreferStoredAlt` keeps a stored alt and only falls back to the pack emoticon.
* **`stickerSet.hash` must be non-zero and content-derived** (`StickerSetHashHelper`). Zero is the
  client's "nothing cached" sentinel, so a set answered with `hash = 0` is re-fetched on every poll.
  It must exclude the per-session `access_hash`/`file_reference`, or no two sessions ever agree.

```bash
# 1. download from Telegram (reuses an authorized Telethon session)
TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \
  python3 scripts/seed_emoji_categories.py --download
# 2. import the icon set + taxonomy, 3. repair alt values (--dry-run to preview)
MONGO_URL=mongodb://172.23.0.8:27017 MINIO_ENDPOINT=172.23.0.10:9000 \
MINIO_ACCESS_KEY=... MINIO_SECRET_KEY=... \
  python3 scripts/seed_emoji_categories.py --import
  python3 scripts/seed_emoji_categories.py --fix-alts
```

Clients cache `TYPE_EMOJI` in `stickers_v2` and refresh it at most hourly
(`MediaDataController.checkStickers`), so after a re-seed either wait an hour or clear the client cache.

### The curated pickers (`account.getDefault*Emojis`)

Three methods hand out lists of custom-emoji ids rather than sets: `getDefaultProfilePhotoEmojis`
(what you can wear as an avatar), `getDefaultGroupPhotoEmojis` and `getDefaultBackgroundEmojis`
(the pattern behind an accent colour). They are **curated by the server** — nothing derives them from
the installed sets — so they are copied from Telegram verbatim: 206 / 208 / 30 ids drawn from 41
packs, measured against the live service. Stored in `default_emoji_lists`, served by
`IDefaultEmojiListAppService`, seeded by `scripts/seed_default_emoji_lists.py`
(`--download` then `--import`, both idempotent, `--dry-run` supported).

* All three used to answer an empty `emojiList` with `hash = 0`, which is why those pickers came up
  blank while the rest of the emoji UI worked.
* **`emojiList.hash` is the server's to define and must not be zero** for a non-empty list:
  `MediaDataController.loadAvatarConstructor` quotes it straight back (`req.hash = emojiList.hash`),
  so zero — the "nothing cached" value — could never match. `EmojiListHashHelper` runs the
  [documented algorithm](https://corefork.telegram.org/api/offsets#hash-generation) over the ids with
  an **unsigned** accumulator; `MessageSearchMongoHelper.CalcHash` shifts a signed one and disagrees
  with every client. An empty list hashes to 0 on purpose, so a client with an empty cache is never
  told its nothing is current.
* **Android re-checks at most once every 24 hours** and caches the response — including an empty one —
  in the `avatar_constructor<account>` preferences. After seeding, a client that already asked keeps
  the blank picker for up to a day unless its data is cleared.
* An id whose document is missing is **dropped** rather than served: `getCustomEmojiDocuments` would
  not resolve it and the client would draw a blank tile.
* The referenced packs are mirrored **whole**, so long-pressing a tile opens the same pack Telegram
  shows. One of Telegram's own sets (`7173162320003085`) is unresolvable on Telegram itself, so its
  documents are imported alone and keep that dangling reference, exactly as Telegram serves them.
* Documents are imported with `free = true` although Telegram marks most of them `free = false`
  (Premium-only): a client honours that by locking the tile, which on a server with no Premium would
  leave the picker as useless as an empty list.

See https://corefork.telegram.org/api/emoji-categories and
https://corefork.telegram.org/api/custom-emoji

---

## Custom emoji in messages

`messageEntityCustomEmoji` carries only a `document_id`; everything a client draws comes back from
`messages.getCustomEmojiDocuments`, and the entity itself is checked on the way in by
`MessageEntityService.ProcessCustomEmojiAsync`.

* **A malformed entity is ignored, not refused.** The API says the entity "must wrap exactly one
  regular emoji (the one contained in `documentAttributeCustomEmoji.alt`) …, otherwise the server
  will **ignore** it". So an unknown `document_id`, a `document_id` of 0, a document that is not a
  custom emoji, and text that does not match `alt` all drop the entity and let the text through.
  Answering `DOCUMENT_INVALID`/`EMOTICON_INVALID` instead — which is what this used to do — means a
  forward carrying a stale id, or a client whose sticker cache was cleared, cannot send at all.
  Where the referenced set holds the same emoji under another document the id is repointed rather
  than dropped.
* **`message_animated_emoji_max` is the server's to enforce.** It is advertised through
  `help.getAppConfig` (100 here) and documented as the maximum that may be attached, but no client
  checks it — neither tdlib (`MessageEntity.cpp`, `OptionManager.cpp`) nor Android
  (`MessagesController`, `ChatActivityEnterView`) reads the field. Past the limit the extra
  entities are dropped in reading order, same "ignore" semantics as above. With the current numbers
  the check cannot fire — `MessageEntityValidator.MaxEntities` is also 100, so 101 custom emojis are
  already `ENTITIES_TOO_LONG` — it binds as soon as either number moves.
* **`searchCustomEmoji`'s hash is the client's, computed by the client.** tdlib sends
  `get_recent_stickers_hash(found_stickers.sticker_ids_)` — the documented
  [hash generation](https://corefork.telegram.org/api/offsets#hash-generation) over document ids in
  the order the server returned them (`StickersManager::reload_found_stickers`), so
  `EmojiListHashHelper`/`VectorHashHelper` is the only correct answer. It used to be FNV-1a, which
  no client could ever match: `emojiListNotModified` never fired and every tdlib client re-fetched
  the whole id list every 300 s per searched emoji.
* **`getCustomEmojiDocuments` answers positionally.** Duplicates and order belong to the client, so
  only the Mongo query is deduped; the request is capped at 200 ids, the number tdlib marks as the
  server-side limit (`MAX_GET_CUSTOM_EMOJI_STICKERS`) and splits larger requests against.
* **`free = true` on every seeded custom emoji.** Clients lock a non-free tile behind Premium, and
  Android marks the *whole* set premium if one document is not free
  (`MessageObject.isPremiumEmojiPack`), so copying Telegram's `false` through locked `StatusPack`
  and `Topics` outright. Same decision already taken for `account.getDefault*Emojis`.
  `scripts/migrate_custom_emoji_free_flag.py` repairs rows written before that (`--dry-run`).
* **A stickerset reference inside an attribute is stored as `_id` *or* `Id`** — the driver maps a
  member named `Id` onto `_id` even in a subdocument, so both shapes are in the collection.
  `CustomEmojiAttributeHelper` and `StickerAttributeSerializer` both accept either; reading one only
  yields `inputStickerSetID(id = 0)`, invisible until the pack refuses to open.

### Emoji keywords

`emoji_keywords` holds Telegram's own localized lists, copied verbatim by
`scripts/seed_emoji_keywords.py` — 4283 keywords for `en`, 5326 for `ru` (measured). It used to be a
by-product of `seed_stickers.py`, synthesized from stickerset titles and pack emoticons: 124 English
rows whose keywords were mostly the emoji themselves, so searching an emoji by word found nothing.
`seed_stickers.py` no longer touches the collection.

* **`Version` is a revision of the language, not a row counter.** Every row of a language carries
  the `version` Telegram reported. Numbering rows 1..N makes the "version" a client stores the index
  of the last keyword, and a re-seed producing fewer rows can then never reach it through
  `getEmojiKeywordsDifference`.
* **`getEmojiKeywordsLanguages` only names languages that have keywords** (plus `en`, which it is
  documented to always include). A language it claims is a language the client then fetches and
  caches empty — Android for an hour (`MediaDataController.fetchNewEmojiKeywords`).
* **Telegram serves a few keywords twice, differing by a trailing space** ("magic " and "magic").
  Clients trim what the user typed, so the seeder merges them.

```bash
TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \
  python3 scripts/seed_emoji_keywords.py --download        # TG_EMOJI_LANGS defaults to "en,ru"
MONGO_URL=mongodb://172.23.0.8:27017 python3 scripts/seed_emoji_keywords.py --import  # --dry-run ok
```

See https://corefork.telegram.org/api/custom-emoji

---

## Message drafts

A draft is stored on `DialogAggregate` (`DraftSavedEvent` / `DraftClearedEvent`) and read back from
`DraftReadModel` (`getAllDrafts`, `forumTopic.draft`, `monoForumDialog.draft`) and `DialogReadModel`
(`dialog.draft`). Everything else is `SaveDraftHandler`, `ClearAllDraftsHandler` and
`DraftDomainEventHandler`.

* **`updateDraftMessage` is not optional.** Syncing drafts between devices is the whole point, and
  nothing else does it: Android asks for the full list exactly once per account
  (`UserConfig.draftsLoaded` is persisted forever), and tdlib's `clear_all_draft_messages` drops only
  *secret chat* drafts locally — for every cloud dialog it waits for the server to send
  `draftMessageEmpty`. Push through `DomainEventHandlerBase.PushMessageToPeerAsync` rather than
  `IObjectMessageSender` directly, so the update is stored with a `globalSeqNo` and a session that was
  offline still gets it from `updates.getDifference`; `updateDraftMessage` carries no `pts`. The
  originating session is excluded — it applied the draft locally and an echo rewrites the text the user
  is typing (Android posts `newDraftReceived`).
* **The peers travel with the updates.** tdlib feeds `getAllDrafts` into its update manager and repairs
  a draft for a dialog it does not know with `getPeerDialogs`, but only when it has read access to the
  peer (`MessagesManager::on_update_dialog_draft_message`) — that is, only when the user or channel came
  with the answer. `PeerType.Self` is a user too; filtering on `PeerType.User` alone leaves the Saved
  Messages draft unnamed.
* **An empty `saveDraft` is a clear, not an empty draft.** That is how every client drops a cloud draft
  (tdlib `SaveDraftMessageQuery`, Android `MediaDataController.saveDraft`). Empty means: no text, no
  media, no reply target and no `suggested_post`; an `effect` alone is not a draft. A reply with no text
  *is* a draft.
* **`reply_to` carries three unrelated things**: the message being replied to, the forum topic
  (`top_msg_id`) and the monoforum topic (`monoforum_peer_id`). A `reply_to_msg_id` of 0 is **not** a
  reply — tdlib clears a topic draft by sending exactly that, so treating it as one stores a draft that
  can never be cleared again.
* **One draft per topic** (`DraftTopicKey`): the chat level draft keeps the bare `DialogId` as its read
  model id, a topic draft gets `_t{topMsgId}` / `_m{savedPeerId}`. Clients read which one an update is
  about from `top_msg_id` / `saved_peer_id`; tdesktop sends `top_msg_id` for topic drafts
  (`Data::ReplyToForMTP`), Android does not, so a topic draft written from Android is a chat level draft.
* **The owner and the peer come from the command, never from `_state`.** Typing into a chat you have
  never messaged is a draft on a dialog that does not exist yet; reading them off the empty state stored
  it under owner 0 with no peer, invisible to `getAllDrafts`, which filters by owner.
* **`clearAllDrafts` clears through the dialog**, not by deleting draft rows: deleting them left
  `dialog.draft` in place and the next `getDialogs` handed every draft straight back. All topics of one
  dialog travel in one `ClearDraftsCommand` because a `DistinctCommand` hashes **only** the request's
  `msg_id` (not the aggregate id), so a second command for the same dialog in the same request is
  silently skipped.
* **A clear emits only for topics that actually hold a draft.** Clients set `clear_draft` on practically
  every send, and an unconditional event would push a `draftMessageEmpty` to every other session on
  every message.
* **`clear_draft` on send belongs to the user, not to the message box.** The outbox item of a channel or
  group message is owned by the peer itself, so addressing the dialog by `OutboxMessageItem.OwnerPeer`
  cleared a dialog nobody holds a draft in — the draft survived every send in a group. Use
  `RequestInfo.UserId`.
* **`draftMessage.media` is an `InputMedia`**: it is echoed back to the clients, never uploaded, and in
  practice only ever `inputMediaWebPage` (the manual link preview). Registering it on the file server, as
  `saveDraft` used to, uploaded media on every keystroke for a value nothing ever read. A dice stays
  `MEDIA_INVALID` (see Animated dice).
* **`MSG_ID_INVALID` is deliberately not enforced** for the reply target: a draft is not a message, and
  refusing one whose target was deleted makes the draft impossible to save at all, on every keystroke.

Probing needs a throwaway Telethon script (temp dir, not `scripts/`) with two logins of the same
account — A writes, B watches for `updateDraftMessage`. `auth.signIn` flood-waits after a handful of
logins; rather than logging in again, build the session from an auth key that is already signed in
(`eventflow-authkeyreadmodel.Data` → `session.auth_key = AuthKey(bytes)`). Worth asserting:
`getAllDrafts` returns the draft *and* names its peer, the other session gets `draftMessage` /
`draftMessageEmpty`, `dialog.draft` appears and disappears, a forum topic draft and the chat draft
coexist, `forumTopic.draft` is filled, sending into a topic clears that topic only, and a send with no
draft pushes nothing.

See https://corefork.telegram.org/api/drafts

---

## Dialog folders

Four surfaces on one API page: the tab folders (dialog filters), folder tags, shared folders (chatlists)
and peer folders (the archive). Services live in `Services/Folders/`; the folders themselves are
`DialogFilterAggregate`, everything per user is `DialogFilterSettingsAggregate`
(`eventflow-dialogfiltersettingsreadmodel`: `Order`, `TagsEnabled`, `ArchivePinned`).

* **The order of `getDialogFilters` is the contract.** Clients adopt the vector verbatim and number their
  own tabs by position (Android `MessagesStorage.checkLoadedRemoteFilters` assigns `filter.order` from the
  index), so `updateDialogFiltersOrder` has to store it — it used to answer `boolTrue` and drop the vector,
  which undid every reorder on the next start. The vector includes **`0` for `dialogFilterDefault`**
  ("All chats" can be moved), so id 0 owns a slot without owning a folder; a folder created after the last
  reorder is appended, lowest id first.
* **Folder ids below 2 are reserved.** Clients allocate from 2 upwards (`FilterCreateActivity`), 0 is the
  default folder, and `messages.updateDialogFilter` now answers `FILTER_ID_INVALID` below that instead of
  storing a folder that shadows "All chats".
* **`tags_enabled` is per user and behind the subscription.** The live service answers `false` for an
  account without one (measured); it was hardcoded `true` here, which turned folder tags on everywhere,
  and `toggleDialogFilterTags` stored nothing so the toggle could never be turned off.
  `updateDialogFilters` is pushed only when the value actually changed, as the API states.
* **An imported folder is a `dialogFilterChatlist`, and so is one whose owner exported a link.** Android
  reads the pair (constructor, `has_my_invites`) as "shared folder" / "I administer this shared folder";
  the converter used to emit `dialogFilter` for everything and `updateDialogFilter` with a
  `dialogFilterChatlist` threw `NotImplementedException`, which is exactly what a client sends when
  renaming an imported folder. `has_my_invites` is derived per request from `chatlist_invites`.
* **The identity of an imported folder is the slug, not the exporter's `filter_id`.** That number belongs
  to the exporter's account: reusing it overwrote whichever folder of the importer carried the same
  number, and `checkChatlistInvite` reported "already joined" for an unrelated folder.
  `joinChatlistInvite` allocates a free id (`IDialogFilterIdAllocator`) and stores `ImportedFromSlug`.
* **`joinChatlistInvite` must answer with the `updateDialogFilter` it produced** — Android scans the
  returned updates for it to learn the folder id and scroll to the new tab (`FolderBottomSheet`), so an
  empty `updates` leaves the user with nothing to look at. It also joins the channels it added
  (`IChatlistMembershipService`, with `ReqMsgId = 0` so the join saga does not answer this request, and one
  `TempId` per channel so the `msg_id` based command dedupe cannot collapse the batch).
* **The three update methods are a diff against the link**: `getChatlistUpdates` answers the link's peers
  minus the folder's minus the dismissed ones (`chatlist_hidden_updates`), `joinChatlistUpdates` accepts
  only peers that diff currently offers, `hideChatlistUpdates` remembers the rest. All three used to answer
  empty, so a shared folder never picked up a chat its owner added later.
* **Only folders 0 and 1 exist for peers** — "no other folder_id is allowed at the moment" — and
  `folders.editPeerFolders` answers `FOLDER_ID_INVALID` for anything else. A peer with no dialog row is
  dropped: `DialogAggregate.UpdateDialogFolder` asserts the dialog exists, so the command failed, the saga
  kept waiting for the event it counts and **the request was never answered at all**.
* **An absent `folder_id` means folder 0** (measured: `getDialogs()` and `getDialogs(folder_id=0)` are the
  same answer, archived chats only under 1), and a dialog that was never archived stores no `FolderId` —
  so the main list matches `null` too. Without that, archiving a chat left it in both lists.
* **`updateFolderPeers` also goes to the other sessions.** The saga consumes a `pts` for it, so answering
  only the caller left every other device with a gap `updates.getDifference` can never fill.
* **`dialogFolder` is served only for a pinned archive.** The live service sends none for an unpinned one
  (measured with 8 archived chats) and Android builds the row locally
  (`ensureFolderDialogExists`); `toggleDialogPin` with an `inputDialogPeerFolder` stores the flag, and the
  push carries no `order` so clients re-read `getPinnedDialogs` instead of dropping their other pins.
* **`getSuggestedDialogFilters` serves six entries and suppresses the ones already built**, comparing flag
  sets exactly — measured: an account with groups-only, channels-only and bots-only folders was offered
  neither, but still got `Personal` while owning contacts-only *and* non-contacts-only folders. `Unread` and
  `Personal` are verbatim; the other four descriptions are extrapolated from them and are the one part of
  the surface that is not measured (a fresh prod account with no folders would show the rest).
* **Pushes exclude `RequestInfo.PermAuthKeyId`, not `AuthKeyId`**: with PFS a request arrives on a
  temporary key, so excluding the temporary id fails to exclude the originating session and it is told
  about the folder it just wrote itself.

See https://corefork.telegram.org/api/folders and
https://corefork.telegram.org/api/links#chat-folder-links

---

## Top peer rating

The frequently-messaged row in search, the "@" inline strip, the mini-app strip and the call
destinations are one surface: `contacts.getTopPeers`, `resetTopPeerRating`, `toggleTopPeers`.
Everything lives in `Services/TopPeers/`; per-user state is `top_peers_settings` (opt-out),
`top_peers_excluded` (resets) and `top_peer_usage` (recorded uses, TTL = the 90-day rating window).

* **The category order is a wire contract.** tdlib asks for every category at once and hashes
  `get_vector_hash` over the bare peer ids of **all** its cached categories concatenated in
  `TopDialogCategory` enum order (`do_get_top_peers`), so `TopPeerCategoryHelper.WireOrder` reproduces
  that order — correspondents, botsPM, botsInline, **groups, channels, phoneCalls**, forwardUsers,
  forwardChats, botsApp. Real Telegram serves them in exactly that order (measured). Emit them in any
  other order and the hash can never match.
* **The hash is the client's, computed by the client**, and real Telegram folds exactly the ids it
  returned, in the order it returned them, with the
  [documented unsigned accumulator](https://corefork.telegram.org/api/offsets#hash-generation) —
  measured: `getTopPeers(correspondents, limit=64)`, re-quoted with that fold, answers
  `topPeersNotModified`, and the same fold over the reversed list does not. Truncation does not change
  it (the category had `count=109` behind a `limit=64`). Only two client families send a real hash:
  tdlib (all categories at once) and tdesktop (`HashInit/HashUpdate/HashFinalize` over
  `peerToUser(...).bare`, `take 64`). Android, iOS, macOS, tweb and telegram-tt send `0`.
  `TopPeersHashHelper` therefore folds through `VectorHashHelper`;
  `MessageSearchMongoHelper.CalcHash` shifts a **signed** long and disagrees with every client as soon
  as the accumulator goes negative.
* **We answer every requested category, empty ones included — real Telegram does not** (measured: nine
  flags, seven categories back, `botsPM` and `groups` simply absent). This is a deliberate deviation:
  tdlib clears its cached copy only for the categories present in the response, so once a category
  empties out, an omitted one keeps stale ids in the vector tdlib hashes and its
  `topPeersNotModified` can never fire again. No client minds an empty category — tweb guards on
  `categories.length`, tdesktop compares the constructor, telegram-tt looks the category up by type,
  and Android's catch-all branch just sets empty hints. Revert by skipping empty categories in
  `GetTopPeersHandler` if matching prod byte-for-byte ever matters more.
* **`getTopPeers` flood-waits hard on real Telegram** — a handful of calls in a minute bought a
  3389-second wait. Budget one or two calls per probe run, or the cross-category comparison cannot be
  finished at all.
* **`rating` is on the clients' scale, or their local increments are meaningless.** Clients add
  `exp((used - rating_timestamp) / config.rating_e_decay)` per use on top of what the server sent
  (tdlib `rating_add`, Android `Math.exp(dt / ratingDecay)`), so the server rating is
  `Σ exp((date - now) / rating_e_decay)` with the very same constant —
  `TopPeerRatingConstants.RatingEDecaySeconds`, which `ConfigConverter` also emits, so the two cannot
  drift. `count × exp(-Δ/30 days)` is not that number. tdlib also gates story display on the absolute
  value (`MIN_STORY_RATING = 10`, correspondents only).
* **A reset is per category.** Android sends `topPeerCategoryCorrespondents` from `removePeer`,
  `BotsInline` from `removeInline`, `BotsApp` from `removeWebapp`; iOS and telegram-tt likewise.
  Ignoring `category` means dismissing a bot from the inline strip also erases it from the hints row.
  Rows written before this carry no `Category` and are still read as "every category".
* **A counter the server owns is deleted; a message-derived rating is remembered.** Resetting
  `botsInline`/`botsApp`/`phoneCalls`/`forward*` drops the `top_peer_usage` rows, so the peer may climb
  back — which is what "reset the rating" means. Resetting correspondents/botsPM/groups/channels has to
  persist an exclusion, because the messages behind it are still there and the next refresh would undo it.
* **The five categories no message expresses are recorded explicitly.** `sendInlineBotResult` (the pick,
  not the query — clients fire one per keystroke), the three `requestWebView` methods, `discardCall`
  (both parties, once per call) and `forwardMessages` (once per action, before `drop_author` nulls the
  header out) call `ITopPeerUsageRecorder`. Deriving them from messages ranked them by conversation
  volume instead, and `botsInline` was every bot the user had ever messaged — Android feeds that
  category straight into the "@" strip, so a bot with no inline mode there is a suggestion that cannot
  work. `botfather-bot-state.InlineEnabled` is the filter.
* **Saved Messages and deleted accounts are not correspondents** (Android drops the self peer on both
  read and write), and `PeerType.Chat` never appears: every group here is a channel, and nothing in this
  repo can build a legacy `chat` object to accompany a `peerChat`.
* **`bots_guestchat` (flags.17, `topPeerCategoryBotsGuestChat`) is not in layer 222**, and
  `MyTelegram.Schema` is generated — a client asking for that flag alone gets `TYPES_EMPTY`. tdesktop's
  guest-bot strip does exactly that; iOS pairs it with `bots_inline` and is unaffected.
* **The rating is cached in process for 60 s** (`ITopPeerRatingCache`) and invalidated on every use,
  reset and toggle: tdesktop re-requests on every search-field open (10 s floor) and tdlib re-syncs on
  demand, so this is not a once-a-day call. `idx_message_owner_out_date` covers the aggregation.
  The invalidation is per process, and the recording happens in **command-server** while
  `getTopPeers` is served by **query-server** — so a use is invisible for up to the TTL on the read
  side (measured: a forward appeared 65 s later with rating 0.999962, i.e. exactly
  `exp(-90 / 2419200)`). Harmless for a list clients refresh daily; anything that needs it immediately
  would have to invalidate over RabbitMQ.
* **Both dispatch paths of a forward have to record.** `ForwardMessagesHandler` leaves through
  `StartForwardMessagesCommand` and returns for every ordinary forward; only a scheduled or monoforum
  forward reaches the tail of the method. Recording only at the tail looked correct and silently counted
  nothing — the tell was a `FwdHeader` message in `eventflow-messagereadmodel` with no matching
  `top_peer_usage` row and no warning anywhere.

See https://corefork.telegram.org/api/top-rating

---

## Account deletion

`account.deleteAccount` → `IAccountDeletionService.DeleteAccountAsync`: emits `UserDeletedEvent`
(profile wiped, `IsDeleted = true` — `UserConverterService` turns that into `user.deleted`), releases
every username through `DeleteUserNameCommand`, revokes every device plus `SessionRevokedEvent`,
and drops the 2FA password. **Messages are not deleted** — the official server keeps the other
party's copy too, see the Telegram FAQ.

With a 2FA password that was *not* passed to the method, deletion is delayed by
`App__AccountDeletion__TwoFaDelayDays` (7) when the password is older than a week **and** the account
was online in the last week (`user_status.LastOnline`); otherwise the account goes immediately. The
delayed case parks a record in `account_deletions`, pushes an `updateServiceNotification` with a
`t.me/confirmphone?phone=…&hash=…` link and answers `420 2FA_CONFIRM_WAIT_%d`.
Cancelling: `account.sendConfirmPhoneCode(hash)` → SMS code → `account.confirmPhone` (drops the
record and logs out the session that requested the deletion).

`AccountDeletionBackgroundService` (command server) executes due records and self-destructs accounts
idle longer than their `account.setAccountTTL` period.

**Never deleted:** system users (`PeerKindHelper.IsSystemUserId` — notification 777000, support 569999,
anonymous, group-anonymous, replies), anything with `Support = true`, bots, and the account configured
in `App__SupportUserId`. The check lives in `IAccountDeletionService.IsProtectedFromDeletion`, so it
covers the RPC (`403 USER_RESTRICTED`), the delayed-deletion sweeper and the TTL self-destruct alike.
See https://corefork.telegram.org/api/account-deletion

---

## Third-party bot verification

Official third-party services hand out an extra verification badge to users and chats. Telegram grants
that right out of band, and so does this server: **there is no API that turns a bot into a verifier** —
seed `bot-verifier-settings` yourself. `Icon` must be the `DocumentId` of a custom emoji that exists in
`eventflow-documentreadmodel`, otherwise clients draw nothing.

```bash
docker compose -p mytelegram exec -T mongodb mongosh tg --quiet --eval '
db["bot-verifier-settings"].updateOne(
  { BotId: NumberLong("2020001") },
  { $set: { Icon: NumberLong("5350513349223189212"), Company: "Acme Inc.",
            CustomDescription: "", CanModifyCustomDescription: true } },
  { upsert: true })'
```

Everything else is `Services/Bots/BotVerificationStore.cs` + `BotVerificationCache.cs`:

* The badge shows up in the official client only through `userFull.bot_info.verifier_settings` — that
  flag is what `ChatEditActivity` gates the whole "Verify Accounts" screen on, so the owner has to open
  the **bot's** profile, not their own.
* `bots.setCustomVerification` may be called by the bot itself (`bot` unset) or by its owner (`bot` set).
  A bot with no verifier settings, or with `Icon = 0`, gets `403 BOT_VERIFIER_FORBIDDEN`.
* Only users and channels can be verified — `chat`/`chatFull` have no `bot_verification` field, so a
  legacy group is `400 PEER_ID_INVALID`. Only the bot that issued a badge can revoke it.
* Description precedence: the bot's `custom_description` (only when `CanModifyCustomDescription`, max
  `bot_verification_description_length_limit` = 70 UTF-8 bytes, else `400 DESCRIPTION_TOO_LONG`), then
  the organisation's `CustomDescription`, then `Was verified by organization "X"`.
* The method answers `Bool`, so it also pushes `updateUser` / `updateChannel` — clients decide between
  "verify" and "remove verification" from the cached `bot_verification_icon`.
* `user.bot_verification_icon` in lists comes from the in-process `IBotVerificationCache`
  (30 s refresh); `users.getFullUser`, `channels.getFullChannel` and `messages.checkChatInvite` read the
  collection directly and are never stale.

See https://corefork.telegram.org/api/bots/verification

---

## Telegram Passport

The server is a **blind relay**: every document is encrypted client-side with a `passport_secret` that
is itself encrypted with the 2FA password, so nothing here can be decrypted. Services live in
`Services/Passport/`.

* **Passport secret** — `account.getPassword` must return
  `securePasswordKdfAlgoPBKDF2HMACSHA512iter100000` in `new_secure_algo` (an `Unknown` algo makes every
  official client abort with "update your app"). The encrypted secret arrives through
  `account.updatePasswordSettings.new_secure_settings`, is kept on `user-password`
  (`SecureAlgoSalt`/`SecureSecret`/`SecureSecretId`) and is handed back by
  `account.getPasswordSettings`. Removing the password destroys all Passport data.
* **`secureValue.hash`** is defined by the *server*: clients read it from the response and quote it
  verbatim in `account.acceptAuthorization.value_hashes` and in `secureValueError.hash`. Plain
  phone/email hash the plaintext (matching tdlib's `calc_value_hash`); everything else hashes the
  `(data_hash, secret)` pairs. `PassportValueStore.ComputeValueHash`.
* **Files** — uploaded with `upload.saveFilePart`, snapshotted out of `file_parts` into
  `passport_file_parts` by `PassportFileStore` (a client reusing its file id must not rewrite a stored
  document), downloaded through `upload.getFile` + `inputSecureFileLocation`. The gate on download is
  the session-derived access hash, **not** ownership — the bot reads the same blob.
* **Bot public key** — BotFather `/setpublickey` (`PassportPublicKey` on `botfather-bot-state`).
  `account.getAuthorizationForm` / `acceptAuthorization` answer `PUBLIC_KEY_REQUIRED` when the bot has
  no key or the quoted one does not match.
* **Scope** — `PassportScopeParser` turns the `UriPassportScope` JSON (`pd`/`pp`/`dl`/… plus the `idd`
  and `add` group aliases and the `s`/`t`/`n` flags) into `SecureRequiredType`.
* **Submission** — `acceptAuthorization` sends one service message to the bot. `MessageServiceMapper`
  renders it as `messageActionSecureValuesSentMe` for the bot and as `messageActionSecureValuesSent`
  (types only) for the sender's own copy.
* **`help.getPassportConfig`** — `Resources/passport-countries-langs.json`, overridable through
  `App__Passport__CountriesLangsFile`. It is served as **compact** JSON: tdlib looks a country up by
  searching the raw string for `"CC":"`, so a space after the colon makes every lookup miss.

See https://corefork.telegram.org/api/passport and
https://corefork.telegram.org/passport/encryption

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

## Temporary auth keys (PFS)

Perfect Forward Secrecy means the client talks over a **temporary** auth key bound to its permanent
one with `auth.bindTempAuthKey`. Only permanent keys are persisted (`eventflow-authkeyreadmodel`,
which carries `Data`/`ServerSalt`/`AccessHashKeyId`); temp keys live in a `ConcurrentDictionary`
inside session-server and are swept by a `PeriodicTimer` once
`App__TempAuthKeyExpirationMinutes` have passed since the bind. They are in **no** durable store —
not the read model, not Redis — so a session-server restart also invalidates every one of them.

The server picks that lifetime itself and ignores the `expires_at` the client sent, so the setting
must not be shorter than the client's own temp-key lifetime. Android binds with
`expires_at = now + TEMP_AUTH_KEY_EXPIRE_TIME` = **24 h** (`tgnet/Defines.h`) and gives the key up
only when a request is answered with `-404`, so with the image default of 720 min a phone that
stays offline overnight returns with a key the server has forgotten. Hence
`App__TempAuthKeyExpirationMinutes=1500` (25 h) in `.env`.

The symptom is *partial* breakage rather than a dead client, which is what makes it confusing:
`ConnectionsManager::processServerResponse` re-handshakes on `-404` only when that datacenter is not
already handshaking, and only for the connection's own key kind
(`HandshakeTypeTemp` vs `HandshakeTypeMediaTemp`), so the connections that lose the race keep
re-sending on the dead key. Media downloads sit at "0 KB / 49.8 KB" forever while the rest of the UI
works, and session-server logs

```
[WRN] ConnectionId="…" authKeyId=6a68d3153cd692a (479225653360421162) authKey not found,
      sending auth key not found message to client
```

with **zero** `RequestGetFile` lines — the request dies at the auth-key check before dispatch, so
there is no `DOCUMENT_INVALID` and nothing at all in the file-server log to look at.

```bash
# is a failing key temp (absent) or perm (present)?
docker compose -p mytelegram exec -T mongodb mongosh tg --quiet --eval \
  'printjson(db["eventflow-authkeyreadmodel"].findOne({AuthKeyId: NumberLong("479225653360421162")}))'
# when was it last accepted, when did it start failing?
docker compose -p mytelegram logs -t --since 24h session-server | grep 6a68d3153cd692a
```

See https://corefork.telegram.org/api/pfs

---

## Debugging

| Symptom | What to check |
|---------|---------------|
| Handler is not invoked | `internal sealed class`, namespace `...LatestLayer.<Category>`, was the image rebuilt |
| Empty/wrong response | uninitialized `TVector`, the data in Mongo, `logs \| grep -i error` |
| Client crashes on the response | constructor ID match (`/schema-jppgr-am`), unset required fields |
| Android dies at startup with `ArrayIndexOutOfBoundsException: length=6; index=6` in `MediaDataController.processLoadedStickers` | `config.preload_featured_stickers` is set while a trending emoji list is non-empty; the flag must stay off, see Stickers above. The stack trace is the only evidence — the server logs a perfectly healthy `getFeaturedEmojiStickers` |
| Half the media never loads | `authKey not found` in session-server — an expired temp key, see PFS above |
| A draft shows up on one device only, or comes back after being cleared | no `updateDraftMessage` was pushed, or it was pushed without a `globalSeqNo`; check that the clear went through the dialog (`DraftClearedEvent`) and not around it, see Message drafts above |
| An archived chat is still in the main list, or the archive looks empty | `getDialogs` folder predicate: an absent `folder_id` and `0` both mean the main list, and a dialog that was never archived stores no `FolderId` — see Dialog folders above. A second device that never learns about the archiving is the missing `updateFolderPeers` push |
| Folder tabs revert their order, or folder tags turn themselves on | `eventflow-dialogfiltersettingsreadmodel` for that user: no `Order` means `updateDialogFiltersOrder` never reached the aggregate, and `TagsEnabled` is what `messages.dialogFilters.tags_enabled` must report — never a constant |
| Part of a screen is empty, logs are clean | diff the surface's methods against **real Telegram** (see Verify above); an empty list with `hash = 0` logs nothing but gets re-requested forever, so a handler called far more often than its refresh interval is the tell |
| The "@" strip suggests bots that answer nothing, or a bot dismissed from one strip vanishes from another | top peers: `botsInline` must be filtered by `botfather-bot-state.InlineEnabled`, and `resetTopPeerRating` must honour `obj.Category` — see Top peer rating above |
| `contacts.getTopPeers` is called on every search open and never answers `topPeersNotModified` | the hash, the category order, or an omitted empty category — all three make tdlib's and tdesktop's hash unmatchable, and none of them logs anything |
| Which connection is actually the user's | gateway `New client connected` by remote IP × `LocalPort`: `172.23.0.1` is the docker host (local scripts), an IP that only sends `req_pq_multi` and drops is a scanner — do not diagnose from their warnings |
| Telethon cannot talk to the server at all | it ships Telegram's public keys and cannot match the fingerprint in our `res_pq`, so the handshake dies at step 1 with `auth_key generation failed` while auth-server logs only `[Step1] ReqPqMultiHandler` retries. Register the server key: `docker cp <auth-server>:/app/private.pkcs8.key`, `openssl rsa -pubout \| openssl rsa -pubin -RSAPublicKey_out`, then `telethon.crypto.rsa.add_key(pem, old=False)` — see `scripts/verify_stickers.py` |
| Telethon connects, then hangs and drops | server→client msg_ids arriving ~743 days in the future, which Telethon discards (`Server sent a very new message`). **Check which host you are actually talking to** — this was traced to a *different* testgram deployment (`109.107.181.246`), not this one, where the skew is 0. A deployment shows it when its session-server carries the pre-fix `MessageIdHelper`, whose `last + 4` branch can never return to the clock; widen `MSG_TOO_NEW_DELTA`/`MSG_TOO_OLD_DELTA` in `telethon/network/mtprotostate.py` to probe such a host |
| Clicking an animated emoji animates nothing on the other device | the interaction is relayed but its `msg_id` belongs to the clicking user's own numbering — see Animated emojis above; both sides log a healthy `setTyping`/`updateUserTyping`, so the only evidence is the id itself |
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
