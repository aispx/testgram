# Testgram

[![API Layer](https://img.shields.io/badge/API_Layer-224-blueviolet)](https://corefork.telegram.org/methods)
[![MTProto](https://img.shields.io/badge/MTProto_Protocol-2.0-green)](https://corefork.telegram.org/mtproto/)
[![Fork](https://img.shields.io/badge/fork-loyldg%2Fmytelegram-blue)](https://github.com/loyldg/mytelegram)

**Testgram** — форк [MyTelegram](https://github.com/loyldg/mytelegram), самохостируемая реализация серверной части Telegram на C#.

## Поддерживаемые функции

### Открытые функции
- API Layer: `224`
- MTProto транспорты: `Abridged`, `Intermediate`
- Личные чаты
- Супергруппы
- Каналы
- Реакции на сообщения
- Star Gifts (каналы, скрыть/показать, непрочитанные упоминания)
- Вход через Passkey (WebAuthn)
- Директ канала (Monoforum)
- Поддержка ботов
- Истории
- Настройки приватности и двухфакторная аутентификация
- Голосовые и видеозвонки (WebRTC)
- Telegram Business
- Автоудаление сообщений
- Стикеры
- Отложенные сообщения
- Темы форума
- Темы оформления и обои
- Папки (фильтры диалогов)

### Скоро...
- Сквозное шифрование
- Вход через email
- Push-уведомления (Firebase)

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

### Настройка голосовых и видеозвонков

Голосовые и видеозвонки **требуют** TURN/STUN сервер. Установите Coturn:

```bash
sudo apt-get install coturn
# Настройте /etc/turnserver.conf (см. docs/CALLS_SETUP.md)
sudo systemctl start coturn
```

Настройте WebRTC в `.env`:

```bash
# ОБЯЗАТЕЛЬНО для работы звонков
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123
```

Настройка индексов MongoDB (автоматически при первом запуске):

```bash
cd scripts && ./setup_call_indexes.sh  # Опционально: ручная настройка
```

См. [docs/CALLS_SETUP.md](docs/CALLS_SETUP.md) для полной инструкции по настройке.

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

В репозитории есть Telegram-бот (`bot/`), который слушает коды регистрации через RabbitMQ и отправляет их пользователям.

```bash
cd bot
cp .env.example .env
# Отредактируйте .env: укажите BOT_TOKEN и RABBITMQ_URL
python3 bot.py
```

## Админ: Выдать звёзды пользователю

Подключитесь к MongoDB и выполните:

```js
// mongosh tg

db['star-transactions'].insertOne({
  UserId: Long('USER_ID'),
  Amount: 1000,          // количество звёзд
  Gift: false,
  Title: 'Admin top-up',
  PeerUserId: 0,
  Date: new Date()
});

db['eventflow-userreadmodel'].updateOne(
  { UserId: Long('USER_ID') },
  { $inc: { StarsBalance: 1000 } }
);
```

> Замените `USER_ID` на нужный ID пользователя (найти через `db['eventflow-userreadmodel'].find({UserName: 'username'})`).

---

## Админ: Добавить подарки (Star Gifts)

Подарки хранятся в коллекции `star-gifts`. Чтобы добавить новый подарок:

```js
// mongosh tg

db['star-gifts'].insertOne({
  GiftId: Long('UNIQUE_GIFT_ID'),   // уникальный ID (например, 1001)
  Stars: 50,                         // цена в звёздах
  Title: 'My Gift',
  Description: '',
  DocumentId: Long('DOCUMENT_ID'),   // ID стикера/документа из Telegram
  LimitedQuantity: 0,                // 0 = безлимитный
  SoldCount: 0,
  Available: true,
  FirstSaleDate: new Date(),
  LastSaleDate: null
});
```

Чтобы выдать подарок пользователю напрямую (без покупки):

```js
db['saved-star-gifts'].insertOne({
  UserId: Long('RECIPIENT_USER_ID'),
  FromUserId: Long('0'),
  GiftId: Long('UNIQUE_GIFT_ID'),
  Stars: 50,
  Message: '',
  Saved: true,
  Date: new Date()
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

Каждый уникальный подарок получает 3 атрибута: `model` (стикер), `backdrop` (фон), `pattern` (узор).
Добавьте варианты в `star-gift-upgrade-config`:

```js
db['star-gift-upgrade-config'].insertMany([
  // Модель (вариант стикера)
  {
    gift_id: Long('GIFT_ID'),   // 0 = применяется ко всем подаркам
    type: 'model',
    name: 'Редкая модель',
    rarity_permille: 100,       // 100 = 10% шанс (из 1000)
    document_id: Long('STICKER_DOCUMENT_ID')
  },
  // Фон (цвета)
  {
    gift_id: Long('GIFT_ID'),
    type: 'backdrop',
    name: 'Золотой',
    rarity_permille: 50,
    backdrop_id: 1,
    center_color: 0xF1C40F,
    edge_color: 0xD4AC0D,
    pattern_color: 0xF9E79F,
    text_color: 0xFFFFFF
  },
  // Узор (стикер-оверлей)
  {
    gift_id: Long('GIFT_ID'),
    type: 'pattern',
    name: 'Звёзды',
    rarity_permille: 200,
    document_id: Long('PATTERN_DOCUMENT_ID')
  }
]);
```

> `rarity_permille` — вес из 1000 (больше = чаще выпадает). `gift_id: 0` — атрибуты для всех подарков.

**3. Принудительный апгрейд подарка пользователю (админ):**
```js
// Найти сохранённый подарок
db['saved-star-gifts'].findOne({ OwnerUserId: Long('USER_ID'), IsUnique: false });

// Сделать апгрейд бесплатным и дать пользователю апгрейднуть самому
db['star-gifts'].updateOne(
  { GiftId: Long('GIFT_ID') },
  { $set: { UpgradeStars: 0 } }
);
```

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
