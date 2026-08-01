# Testgram

[![API Layer](https://img.shields.io/badge/API_Layer-224-blueviolet)](https://corefork.telegram.org/methods)
[![MTProto](https://img.shields.io/badge/MTProto_Protocol-2.0-green)](https://corefork.telegram.org/mtproto/)
[![Fork](https://img.shields.io/badge/fork-loyldg%2Fmytelegram-blue)](https://github.com/loyldg/mytelegram)
[![Testgram Channel](https://img.shields.io/badge/Subscribe-_Testgram_Channel-0088cc)](https://t.me/testgramrofl)
[![Testgram Discussion Group](https://img.shields.io/badge/Join_-Testgram_Discussion_Group-0088cc)](https://t.me/+etFTfnAPU7Q1M2Ri)

**Testgram** — форк [MyTelegram](https://github.com/loyldg/mytelegram), самохостируемая реализация серверной части Telegram на C#.

## Поддерживаемые функции

### Открытые функции
- API Layer: `224`
- MTProto транспорты: `Abridged`, `Intermediate`
- Личные чаты
- Супергруппы
- Каналы
- Реакции на сообщения
- Сквозное шифрование чатов (Secret Chats)
- Star Gifts (каналы, скрыть/показать, непрочитанные упоминания, аукционы, коллекции, крафтинг)
- Апгрейды Star Gifts и уникальные подарки (NFT темы)
- Перепродажа Star Gifts за TON / Stars
- Вход через Passkey (WebAuthn)
- Директ канала (Monoforum / платные сообщения)
- Поддержка ботов (включая BotFather, бизнес-ботов, аффилированные / Star Ref боты)
- Истории (включая альбомы, трансляции)
- Настройки приватности и двухфакторная аутентификация
- Голосовые и видеозвонки (1:1, WebRTC)
- Групповые звонки / голосовые и видеочаты
- Конференц-звонки (сквозное шифрование)
- Трансляции (RTMP / HLS)
- Push-уведомления (APNS / FCM / WebPush)
- Отправка email, верификация email и восстановление 2FA через email
- Telegram Business
- Автоудаление сообщений
- Стикеры (включая наборы стикеров, кастомные эмодзи)
- Отложенные сообщения
- Темы форума
- Темы оформления и обои
- Папки (фильтры диалогов) и общие папки (chatlists)
- Сохранённая музыка (Saved Music)
- Статистика каналов (stats.*)
- Журнал действий администраторов
- Списки задач (совместные чек-листы)
- Факт-чек
- Розыгрыши (Giveaways) и бусты
- Несколько username (включая Fragment NFT username)
- Языковые пакеты (включая русский)

### Скоро...
- Полноценный вход через email (флоу верификации email реализован; вход напрямую по email-адресу пока не поддерживается)

---

## Запуск сервера Testgram

### Быстрый старт через Docker

1. Скачайте файлы Docker Compose:

```
curl -O https://raw.githubusercontent.com/glebxdlolreal/testgram/dev/docker/compose/docker-compose.yml
curl -O https://raw.githubusercontent.com/glebxdlolreal/testgram/dev/docker/compose/.env.example
cp .env.example .env
```

2. Отредактируйте `.env`:
   - Замените `YOUR_SERVER_IP` на публичный IP вашего сервера
   - Установите надёжные пароли вместо `CHANGE_ME` (RabbitMQ, Minio, ключи шифрования)

3. Запустите сервер:

```bash
mkdir -p ./data/mytelegram
chmod -R a+w ./data/mytelegram
docker compose up -d
```

### Конфигурация

Основные параметры `.env`:

| Переменная | Описание |
|------------|----------|
| `App__DcOptions__0__IpAddress` | Публичный IP сервера |
| `RabbitMQ__Connections__Default__Password` | Пароль RabbitMQ |
| `App__AccessHashSecretKey` | Случайный секретный ключ |
| `App__EncryptionConfig__MessageKeys__0__Key` | Ключ шифрования в Base64 |
| `App__FixedVerifyCode` | Фиксированный SMS-код для тестирования (оставьте пустым в продакшене) |
| `App__PasskeyRpId` | Relying Party ID для входа через Passkey (WebAuthn) |
| `App__EnableEmailLogin` | Включить флоу верификации email при входе |
| `App__Stripe__SecretKey` | Секретный ключ Stripe (для платной покупки подарков за звёзды) |
| `EmailSenderOptions__SmtpEmailOptions__*` | Настройки SMTP для верификации email и восстановления 2FA |

### Настройка голосовых и видеозвонков

Звонки **требуют** TURN/STUN сервер. В актуальной версии он уже **встроен**: стек
`docker compose` включает сервис `coturn` (STUN/TURN) и сервис `rtmp-server`
(`mediamtx`, используется для трансляций в групповых звонках). Устанавливать Coturn
на хост больше не нужно — достаточно указать в конфиге WebRTC IP вашего сервера.

Настройте WebRTC в `.env` (учётные данные **должны совпадать** с пользователем
встроенного coturn, по умолчанию `testgram:testgram2024`):

```bash
# ОБЯЗАТЕЛЬНО для работы звонков
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram2024
```

Групповые звонки и трансляции используют встроенный RTMP/HLS сервер:

```bash
App__RtmpStreamUrl=rtmp://YOUR_SERVER_IP:1935/live
App__RtmpHlsUrl=http://rtmp-server:8888/live
RTMP_PORT=1935
RTMP_HLS_PORT=8888
```

Индексы MongoDB для звонков создаются **автоматически** при первом запуске через
контейнер `call-init`. Чтобы выполнить вручную:

```bash
cd scripts && ./setup_call_indexes.sh  # Опционально: ручная настройка
```

Откройте нужные UDP/TCP порты в файрволе: `3478` (STUN/TURN), `49152-49172/udp`
(TURN relay) и `1935` (RTMP). См. [docs/CALLS_SETUP.md](docs/CALLS_SETUP.md) для полной
инструкции, включая использование внешнего TURN-сервера.

## Устранение неполадок

### У клиентов `ConnectionRefusedError` (не удаётся подключиться к серверу)

Если клиент не подключается с ошибкой вида:

```
Attempt 1 at connecting failed: ConnectionRefusedError: [WinError 1225] The remote computer refused the network connection
```

но при этом сам VDS/хост доступен — скорее всего, шлюз (gateway) не слушает главный
порт **20443** (DC1, первый порт, к которому подключаются клиенты — см. `App__DcOptions__0__Port`).

Причина: параметр `App__Servers__0__Enabled` не задан/закомментирован в `.env`.
docker-compose всё равно передаёт эту переменную в контейнер шлюза, поэтому незаданное
значение превращается в **пустую строку**. Из-за пустого значения .NET полностью
выбрасывает server 0 из конфигурации, шлюз не открывает слушатель на 20443, и все
подключения отклоняются.

Решение: убедитесь, что в `.env` есть активная строка (не закомментирована и не пустая):

```bash
App__Servers__0__Enabled=True
```

Затем пересоздайте шлюз и проверьте, что он слушает 20443:

```bash
cd docker/compose
docker compose up -d --force-recreate gateway-server
docker compose logs gateway-server | grep 20443   # ожидается: "Tcp server started at ...:20443"
```

### file-server спамит `Bucket name cannot be empty` / не грузятся медиа и иконки верификации

Если в логах `file-server` спам вида:

```
Minio.Exceptions.InvalidBucketNameException: MinIO API responded with message=Bucket name cannot be empty.
```

а в клиентах не загружаются аватарки, стикеры или кастомные иконки верификации — значит,
`Minio__BucketName` не задан/закомментирован в `.env`. docker-compose всё равно передаёт эту
переменную в file-server, поэтому незаданное значение превращается в пустую строку, и любой
запрос файла падает с ошибкой.

Решение: убедитесь, что в `.env` есть активные строки (не закомментированы и не пустые):

```bash
Minio__BucketName=tg-files
Minio__CreateBucketIfNotExists=True
```

Затем пересоздайте file-server:

```bash
cd docker/compose
docker compose up -d --force-recreate file-server
```

### file-server спамит `NullReferenceException` в `MinioStoringHelper.GetAsync` / зависают загрузки

Если логи `file-server` завалены ошибками вида:

```
[ERR] Get file failed, input: FileId: "..." Offset: ... Limit: 32768
System.NullReferenceException: Object reference not set to an instance of an object.
   at Minio.MinioClient.ParseWellKnownErrorNoContent(ResponseResult response)
   ...
   at MyTelegram.FileServer.Services.MinioStoringHelper.GetAsync(...)
```

это регрессия в MinIO .NET SDK, встроенном в сторонний образ `mytelegram-file-server`
(Minio 6.0.6-local). Когда MinIO отвечает на запрос диапазона байт кодом
`416 Range Not Satisfiable` (без тела) — а клиенты Telegram делают такой запрос для
последнего чанка загрузки (offset на/за концом файла) — SDK не обрабатывает 416,
оставляет объект ошибки null, и `throw error;` превращается в NullReferenceException.

Так как file-server собирается и публикуется отдельно, пропатчить его из этого репозитория
нельзя. Вместо этого file-server ходит в MinIO через сервис **minio-proxy** (небольшой
прокси на nginx), который превращает такие ответы 416 в чистый пустой 200, понятный SDK.
Весь остальной трафик проходит без изменений.

Это включено по умолчанию (`Minio__FileServerEndpoint` = `minio-proxy:9000`). Если видите
эту ошибку — убедитесь, что прокси запущен, а file-server ходит через него:

```bash
cd docker/compose
docker compose up -d minio-proxy
docker compose up -d --force-recreate file-server
```

## Сборка Docker-образов

```bash
# Linux amd64
cd build/docker && ./build-all-amd64.sh

# Linux arm64
cd build/docker && ./build-all-arm64.sh
```

## Клиенты

| Платформа | Репозиторий |
|-----------|-------------|
| Android | https://github.com/glebxdlolreal/testgram-android |
| Desktop (TDesktop) | https://github.com/glebxdlolreal/testgram-tdesktop |
| iOS | https://github.com/loyldg/mytelegram-iOS |
| WebK | https://github.com/loyldg/mytelegram-webk |
| WebA | https://github.com/loyldg/mytelegram-weba |

### Настройка клиентов
1. Склонируйте исходный код клиента.
2. Найдите `YOUR_SERVER_IP` во всех файлах и замените на IP вашего сервера.

## Бот верификации

В репозитории есть Telegram-бот (`bot/`), который слушает коды регистрации через RabbitMQ и отправляет их пользователям. Поддерживается запуск нескольких ботов (`BOT_TOKEN`, `BOT_TOKEN1`, `BOT_TOKEN2`, ...) и опциональный SOCKS5/HTTP прокси.

```bash
cd bot
cp .env.example .env
# Отредактируйте .env: укажите BOT_TOKEN (и BOT_TOKEN1...) и RABBITMQ_URL
python3 bot.py
```

## Админ: Выдать звёзды пользователю

Проще всего воспользоваться встроенным скриптом:

```bash
cd scripts && ./give-stars.sh <user_id> <stars_amount>
# Пример: ./give-stars.sh 2010001 1000
```

Скрипт обновляет баланс пользователя в коллекции `star-balances` и записывает
транзакцию в `star-transactions`. Вручную через `mongosh tg`:

```js
// 1. Добавить баланс
db['star-balances'].updateOne(
  { UserId: Long('USER_ID') },
  { $inc: { Balance: 1000 } },
  { upsert: true }
);

// 2. Записать транзакцию
db['star-transactions'].insertOne({
  _id: ObjectId().toString(),
  TransactionId: ObjectId().toString(),
  UserId: Long('USER_ID'),
  Amount: 1000,          // количество звёзд
  Gift: false,
  Refund: false,
  Date: Math.floor(Date.now() / 1000),
  Title: 'Admin top-up',
  PeerUserId: null,
  PeerChannelId: null
});
```

> Замените `USER_ID` на нужный ID пользователя (найти через `db['eventflow-userreadmodel'].find({UserName: 'username'})`).

---

## Админ: Добавить подарки (Star Gifts)

Подарки хранятся в коллекции `star-gifts`. Для создания набора демо-подарков и
вариантов апгрейда используйте встроенный сидер:

```bash
cd scripts && ./init-star-gifts.sh
```

Чтобы добавить один подарок вручную (`mongosh tg`):

```js
db['star-gifts'].insertOne({
  GiftId: Long('UNIQUE_GIFT_ID'),   // уникальный ID (например, 1001)
  Stars: 50,                         // цена в звёздах
  ConvertStars: 40,                  // звёзды при конвертации
  UpgradeStars: null,                // null/0 = не апгрейдится
  Limited: false,                    // true = ограниченный тираж
  SoldOut: false,
  Birthday: false,
  RequirePremium: false,
  LimitedPerUser: false,
  AvailabilityTotal: null,           // всего копий (для limited)
  AvailabilityRemains: null,
  Title: 'My Gift',
  DocumentId: Long('DOCUMENT_ID'),   // ID стикера/документа из Telegram
  DocumentAccessHash: Long('ACCESS_HASH'),
  DocumentDate: Math.floor(Date.now() / 1000),
  MimeType: 'application/x-tgsticker',
  DocumentSize: Long('50000'),
  DcId: 2,
  IsAuction: false,
  RandomId: Long('1')
});
```

Чтобы выдать подарок пользователю напрямую (без покупки):

```js
db['saved-star-gifts'].insertOne({
  OwnerUserId: Long('RECIPIENT_USER_ID'),  // получатель
  FromUserId: Long('0'),
  GiftId: Long('UNIQUE_GIFT_ID'),
  Stars: 50,
  MessageText: '',
  Saved: true,
  IsUnique: false,
  DocumentId: Long('DOCUMENT_ID'),
  Date: Math.floor(Date.now() / 1000)
});
```

---

## Админ: Апгрейды подарков (Star Gift Upgrades)

Чтобы сделать подарок апгрейдируемым:

**1. Установить стоимость апгрейда на подарке:**
```js
// mongosh tg
db['star-gifts'].updateOne(
  { GiftId: Long('GIFT_ID') },
  { $set: {
    UpgradeStars: 1000,        // звёзд для апгрейда
    AvailabilityTotal: 10000   // всего уникальных копий
  }}
);
```

**2. Добавить конфиг апгрейда (атрибуты уникальной версии):**

Каждый уникальный подарок получает 3 типа атрибутов: `model` (стикер), `backdrop` (фон), `pattern` (узор).
Встроенный сидер `scripts/init-star-gifts.sh` добавляет полный набор вариантов в
`star-gift-upgrade-config` (4 модели, 4 фона, 4 узора). Чтобы добавить варианты
вручную (`mongosh tg`):

```js
db['star-gift-upgrade-config'].insertMany([
  // Модель (вариант стикера)
  {
    Type: 'model',
    Name: 'Редкая модель',
    RarityPermille: 100,        // 100 = 10% шанс (из 1000)
    DocumentId: Long('STICKER_DOCUMENT_ID'),
    DocumentAccessHash: Long('ACCESS_HASH'),
    DocumentDate: Math.floor(Date.now() / 1000),
    MimeType: 'application/x-tgsticker',
    DocumentSize: Long('50000'),
    DcId: 2
  },
  // Фон (цвета)
  {
    Type: 'backdrop',
    Name: 'Золотой',
    RarityPermille: 50,
    BackdropId: 1,
    CenterColor: 0xF1C40F,
    EdgeColor: 0xD4AC0D,
    PatternColor: 0xF9E79F,
    TextColor: 0xFFFFFF
  },
  // Узор (стикер-оверлей)
  {
    Type: 'pattern',
    Name: 'Звёзды',
    RarityPermille: 200,
    DocumentId: Long('PATTERN_DOCUMENT_ID'),
    DocumentAccessHash: Long('ACCESS_HASH'),
    DocumentDate: Math.floor(Date.now() / 1000),
    MimeType: 'application/x-tgsticker',
    DocumentSize: Long('40000'),
    DcId: 2
  }
]);
```

> `RarityPermille` — вес из 1000 (больше = чаще выпадает).

**3. Выпустить тему для уникального подарка (админ):**
```bash
cd scripts && ./release-gift-theme.sh <unique_gift_id> <center_color> [edge_color] [pattern_color] [text_color]
# Пример: ./release-gift-theme.sh 1001 0x3390ec 0x6fb1f6 0x8ac5f8 0xffffff
```

---

## Админ: Fragment NFT username

Сервер поддерживает несколько username у пользователя/канала, включая
коллекционные (Fragment NFT) username. Чтобы добавить демо-данные:

```bash
cd scripts && ./init-fragment-nft.sh
```

Чтобы назначить NFT username пользователю (должен существовать в `fragment_collectibles`):

```bash
cd scripts && ./assign-nft-username.sh <user_id> <nft_username>
# Пример: ./assign-nft-username.sh 2010001 blockchain
```

Чтобы добавить collectible вручную (`mongosh tg`):

```js
db.fragment_collectibles.insertOne({
  _id: 'fragment-username-myusername',
  type: 'username',             // "username" или "phone"
  username: 'myusername',
  phone: null,                  // обязательно при type="phone"
  purchase_date: Math.floor(Date.now() / 1000),
  currency: 'USD',
  amount: 14500,                // 145.00 USD
  crypto_currency: 'TON',
  crypto_amount: NumberLong('50000000000'),
  url: 'https://fragment.com/username/myusername'
});
```

NFT username активируются/деактивируются через `account.toggleUsername`,
`channels.toggleUsername` и `bots.toggleUsername`; основной username задаётся
через `account.reorderUsernames` / `channels.reorderUsernames`.

---

## Сидер реакций

После деплоя сервера запустите сидер реакций для заполнения анимаций эмодзи:

```bash
cd scripts

# 1. Скачать файлы реакций из Telegram (~50MB)
TG_API_ID=your_api_id \
TG_API_HASH=your_api_hash \
TG_PHONE=+1234567890 \
python3 seed_reactions.py --download

# 2. Импортировать файлы в Minio + MongoDB
MONGO_URL=mongodb://localhost:27017 \
MINIO_ENDPOINT=localhost:9000 \
MINIO_ACCESS_KEY=your_key \
MINIO_SECRET_KEY=your_secret \
python3 seed_reactions.py --import

# 3. Сгенерировать C#-хендлер с реальными ID документов
MONGO_URL=mongodb://localhost:27017 \
HANDLER_PATH=../source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetAvailableReactionsHandler.cs \
python3 seed_reactions.py --generate-handler

# 4. Пересобрать и задеплоить образы messenger
cd ../build/docker
export REGISTRY_URL="mytelegram"
bash 1.build-messenger-command-server.sh
bash 2.build-messenger-query-server.sh
cd ../../docker/compose && docker compose down && docker compose up -d
```

> **Примечание:** Шаги 1–3 нужно выполнить только один раз. Сгенерированный хендлер коммитится в репозиторий, последующие деплои не требуют повторного сидинга.

## Поддержка Testgram

Если Testgram оказался вам полезен, пожалуйста, поставьте проекту ⭐️.

## Обратная связь

- Связаться с автором: https://t.me/glebxdlol
- Канал Testgram: https://t.me/testgram
- Группа обсуждений: https://t.me/+etFTfnAPU7Q1M2Ri
