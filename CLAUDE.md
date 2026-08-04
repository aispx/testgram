# Testgram Development Guide

Self-hosted C# Telegram server (fork of MyTelegram). MTProto 2.0, API Layer 222.

**Stack:** .NET 10, CQRS + Event Sourcing (EventFlow), MongoDB, RabbitMQ, Redis, MinIO, Coturn
**Solution:** `source/MyTelegram.slnx` · **Tests:** `source/test/*`

---

## Critical Rules

### NO STUBS
**НИКОГДА не делай заглушки без явного запроса.** Если фича требует отсутствующей
инфраструктуры (CDN, FileServer, WebProxy) — **скажи об этом и спроси**, но не возвращай
пустышку:

```csharp
// ❌ ЗАПРЕЩЕНО без явного "сделай заглушку"
return Array.Empty<byte>();
return new TVector<IFileHash>();          // пустой список без причины
throw new NotImplementedException();
_logger.LogWarning("not implemented");    // + возврат дефолта
```

Лучше честный вопрос, чем молчаливая пустышка.

### Security
- **ALWAYS** брать userId из токена: `input.UserId` — никогда из запроса (`obj.UserId` подделывается клиентом)
- **ALWAYS** валидировать вход и access hash
- **NEVER** коммитить `.env` / секреты; **NEVER** использовать публичные STUN в проде

### Data integrity
- **NEVER** писать в `eventflow-*` коллекции напрямую — только через агрегаты/события
- **NEVER** пропускать эмит события в агрегате
- **ALWAYS** `RpcErrors...ThrowRpcError()` вместо `throw new Exception`
- **ALWAYS** инициализировать `TVector<T>` — null роняет клиент

---

## Project Structure

```
source/src/
├── MyTelegram.Messenger/              # Бизнес-логика
│   ├── Handlers/LatestLayer/<Category>/  # RPC handlers (НОВЫЕ СЮДА)
│   ├── Services/                      # Application services
│   └── Converters/                    # Entity → TL мапперы
├── MyTelegram.Schema/                 # TL-сущности (AUTO-GENERATED, не править)
├── MyTelegram.Domain/                 # Агрегаты и события
├── MyTelegram.GatewayServer/          # MTProto gateway
└── MyTelegram.QueryHandlers.MongoDB/  # Read-model запросы

build/docker/    # build-скрипты      docker/compose/  # compose-стек      docs/  # гайды
```

---

## Implementation Workflow

### 1. Research (обязательно, не пропускать)
- `/schema-jppgr-am search <method>` — constructor ID и сигнатура
- https://core.telegram.org/method/<method> — официальная документация
- https://github.com/tdlib/td — референсная реализация сложных фич
- https://github.com/DrKLO/Telegram — поведение клиента/UX
- Веб-поиск: Google Custom Search API или Yandex XML API (встроенный WebSearch ограничен)

### 2. Implementation
Сервисы через конструктор: `IMongoDatabase`, `IUserAppService`, `IMessageAppService`,
`IPeerHelper`, `IQueryProcessor`, `ILogger<T>`.

### 3. Verify
`dotnet test` → пересборка образа → проверка с **официальным** клиентом (не кастомным) →
логи и данные в MongoDB.

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
        // 1. Валидация входа
        if (obj.Stickerset is not TInputStickerSetShortName { ShortName.Length: > 0 } shortName)
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();

        // 2. userId — только из токена
        var userId = input.UserId;

        // 3. Запрос данных
        var collection = database.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var doc = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("ShortName", shortName.ShortName))
            .FirstOrDefaultAsync();

        if (doc == null)
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();

        // 4. Ответ — все TVector инициализированы
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
`input.UserId` · `RpcErrors` · все `TVector` инициализированы · XML-doc со ссылкой на API.
Проверить: `/check-handler <path>`.

### Что считается «не реализовано»

Handler НЕ реализован, если он бросает `NotImplementedException`, возвращает `null!`,
либо отдаёт пустой/дефолтный ответ, не заглянув в данные:

```csharp
// ❌ не реализовано
return Task.FromResult<ISavedMusic>(new TSavedMusic { Count = 0, Documents = [] });
```

Реализован — если валидирует вход, читает реальные данные, использует сервисы,
выполняет операцию и отдаёт ошибки через `RpcErrors`.

---

## Common Patterns

### Валидация пользователя
```csharp
var user = await userAppService.GetAsync(input.UserId);
if (user == null)
    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);   // проверка access hash
```

### Service message + Updates
`SendMessageAsync` работает **асинхронно** через event sourcing и не возвращает созданное
сообщение. Сообщение будет создано и доставлено пушем — но вернуть его в ответе нельзя,
и подделывать объект сообщения в `Updates` нельзя.

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
    Date = CurrentDate,     // Date обязателен
    Seq = 0
};
```
Примеры: `SetHistoryTTLHandler`, `SuggestBirthdayHandler`; `SendMessageHandler` возвращает `null!`.

### Атомарный счётчик ID
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

### Батч вместо N+1
```csharp
// ❌ N+1
foreach (var id in ids)
    await col.Find(f => f["DocumentId"] == id).FirstOrDefaultAsync();

// ✅ один запрос
var docs = await col.Find(Builders<BsonDocument>.Filter.In("DocumentId", ids)).ToListAsync();
var map = docs.ToDictionary(d => d["DocumentId"].AsInt64);
```

### Безопасное чтение Bson
```csharp
private static long GetInt64(BsonValue v) => v.BsonType switch
{
    BsonType.Int64  => v.AsInt64,
    BsonType.Int32  => v.AsInt32,
    BsonType.Double => (long)v.AsDouble,
    _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
};

// FileReference бывает Binary, Array или отсутствует
byte[] fileRef = doc.GetValue("FileReference", BsonNull.Value) switch
{
    { BsonType: BsonType.Binary } b => b.AsBsonBinaryData.Bytes,
    { BsonType: BsonType.Array } a  => a.AsBsonArray.Select(x => (byte)x.AsInt32).ToArray(),
    _ => []
};
```

### Конфиг через IOptions (никаких хардкод IP/портов)
```csharp
public MyHandler(IOptions<MyTelegramMessengerServerOptions> options)
{
    var ip = options.Value.WebRtcConnections[0].Ip;
}
```

---

## TL Schema

| TL | C# | Заметки |
|----|-----|---------|
| `int` / `long` | `int` / `long` | |
| `string` | `string` | UTF-8 |
| `bytes` | `byte[]` | |
| `Vector<T>` | `TVector<T>` | **никогда не null** |
| `flags.N?T` | `T?` | опциональное поле |
| `true` | `bool` | flag-поле |

```bash
/schema-jppgr-am search inputStickerSetItem     # поиск конструктора
/schema-jppgr-am diff 222 223                   # что изменилось между слоями
/schema-jppgr-am layer 222                      # полный слой
/schema-jppgr-am hex2object <hex> 222           # разбор payload
```

**Даты и переполнение int:** timestamp'ы хранить в Mongo как `long`, в TL кастовать:
`ExpireDate = (int)doc["ExpireDate"].AsInt64`. Текущее время —
`(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()`.

**Обязательные поля:** у `TChannel`/`TUser` и т.п. заполнять `Photo = new TChatPhotoEmpty()`,
`RestrictionReason = new TVector<IRestrictionReason>()` — иначе NullReferenceException в клиенте.

---

## MongoDB

| Коллекция | Назначение | Ключевые поля |
|-----------|------------|---------------|
| `eventflow-stickersetreadmodel` | Стикерсеты | StickerSetId, ShortName, Slug, DocumentIds |
| `eventflow-documentreadmodel` | Файлы | DocumentId, AccessHash, FileReference |
| `eventflow-channelreadmodel` | Каналы | ChannelId, UserName, Title |
| `eventflow-userreadmodel` | Пользователи | UserId, Phone, UserName, Usernames |
| `call_sessions` | Звонки | CallId, AccessHash, Date (TTL 30 дней) |
| `stories` | Истории | OwnerPeerId, StoryId, Date |
| `businesschatlinks` | Business links | UserId, Slug (unique) |
| `quickreplys` | Быстрые ответы | UserId, ShortcutId |
| `star-gifts` | Star gifts | GiftId, Stars |
| `fragment_collectibles` | Fragment NFT | type, username/phone |
| `eventflow-*` | Event sourcing | **не менять напрямую** |

```bash
docker compose -p mytelegram exec mongodb mongosh tg

db.getCollectionNames()
db["eventflow-stickersetreadmodel"].findOne({ ShortName: "mypack" })
db["eventflow-documentreadmodel"].find({ DocumentId: NumberLong("123") })
```

---

## Build & Deploy

> Здесь установлен только `docker compose` v2 (не `docker-compose`), и стек называется
> **mytelegram** — всегда передавай `-p mytelegram`, иначе поднимется дублирующий стек
> `compose-*`, чей mongodb уйдёт в crash-loop на `DBPathInUse`.

```bash
# Тесты
dotnet test source/MyTelegram.slnx

# Один сервис
cd build/docker && ./1.build-messenger-command-server.sh
docker compose -p mytelegram up -d messenger-command-server

# Всё
cd build/docker && ./build-all-amd64.sh
docker compose -p mytelegram up -d

# Логи
docker compose -p mytelegram logs -f messenger-command-server
docker compose -p mytelegram logs -f gateway-server | grep -i error

# Чистая пересборка (диск почти полон — сначала docker builder prune -af)
cd scripts && ./delete-bin-obj-folders.sh && cd ../build && ./build.sh
```

Скрипты: `1.` command-server, `2.` query-server, `4.` sms-sender, `5.` gateway-server,
`6.` auth-server, `7.` data-seeder.

---

## Debugging

| Симптом | Что проверить |
|---------|---------------|
| Handler не вызывается | `internal sealed class`, namespace `...LatestLayer.<Category>`, пересобран ли образ |
| Пустой/неверный ответ | неинициализированные `TVector`, данные в Mongo, `logs \| grep -i error` |
| Клиент падает на ответе | совпадение constructor ID (`/schema-jppgr-am`), незаполненные обязательные поля |
| Звонки не работают | `env \| grep WebRtc`, `systemctl status coturn`, `db.call_sessions.find().sort({Date:-1}).limit(5)` |

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

## Fragment / множественные username

Пользователи и каналы могут иметь несколько username:
- **Basic** (`Editable=true`) — обычный, всегда активен, отключить нельзя
- **Fragment NFT** (`Editable=false`) — куплен на Fragment.com, можно включать/отключать

```csharp
public class UsernameInfo   // read model
{
    public string Username { get; set; }
    public bool Editable { get; set; }   // true = basic, false = Fragment NFT
    public bool Active { get; set; }
}
// TL: TUsername { Editable (flag 0), Active (flag 1), Username }
```

`UserMapper` / `ChannelMapper` конвертируют `Usernames` в `TVector<IUsername>` и выставляют
основной `Username` = первый active+editable, иначе первый active; при пустом списке — fallback
на legacy-поле `UserName`. Основной username всегда идёт первым.

**Handlers:** `account.toggleUsername` / `reorderUsernames` (лимит 10 активных, basic отключить
нельзя), `channels.toggleUsername` / `reorderUsernames` / `deactivateAllUsernames`,
`bots.toggleUsername` / `reorderUsernames`.

### fragment.getCollectibleInfo

`Handlers/LatestLayer/Fragment/GetCollectibleInfoHandler.cs` — ищет в `fragment_collectibles`
по `TInputCollectibleUsername` (username в lowercase) или `TInputCollectiblePhone`.
Ошибки: `COLLECTIBLE_INVALID`, `COLLECTIBLE_NOT_FOUND`.

```javascript
{
  _id: "fragment-username-testgram",
  type: "username",            // "username" | "phone" (phone начинается с 888)
  username: "testgram",
  purchase_date: 1704067200,   // unixtime
  currency: "USD",
  amount: 14500,               // Int32, минимальные единицы: 145.00 USD
  crypto_currency: "TON",
  crypto_amount: NumberLong("50000000000"),  // Int64, минимальные единицы TON
  url: "https://fragment.com/username/testgram"
}
```

Клиент (`ProfileActivity` → `FragmentUsernameBottomSheet`) запрашивает этот метод при клике на
username с `!editable` или на телефон, начинающийся с `888`, и показывает дату и цену покупки.

### Выдать NFT username вручную
```bash
docker compose -p mytelegram exec -T mongodb mongosh tg --quiet --eval '
db["eventflow-userreadmodel"].updateOne(
  { UserId: NumberLong("2010001") },
  { $set: { Usernames: [
      { Username: "glebxdlol",  Editable: true,  Active: true },
      { Username: "blockchain", Editable: false, Active: true }
  ] } })'

docker compose -p mytelegram restart messenger-command-server messenger-query-server
# В клиенте: убить процесс, очистить кэш, войти заново
```

---

## Skills

| Skill | Назначение |
|-------|------------|
| `/schema-jppgr-am` | TL-схема: поиск конструкторов, diff слоёв, разбор hex |
| `/check-handler <path>` | Проверка handler'а на типовые ошибки |
| `/test-handler <Name>` | Регистрация, логи, данные в Mongo |
| `/rebuild-service <svc>` | Сборка образа + рестарт + логи старта |

---

## Resources

- API: https://core.telegram.org/api · Методы: https://core.telegram.org/methods · Схема: https://core.telegram.org/schema
- TDLib: https://github.com/tdlib/td · Android: https://github.com/DrKLO/Telegram · TDesktop: https://github.com/telegramdesktop/tdesktop
- Schema API: https://schema.jppgr.am
