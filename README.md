# Testgram

[![API Layer](https://img.shields.io/badge/API_Layer-224-blueviolet)](https://corefork.telegram.org/methods)
[![MTProto](https://img.shields.io/badge/MTProto_Protocol-2.0-green)](https://corefork.telegram.org/mtproto/)
[![Fork](https://img.shields.io/badge/fork-loyldg%2Fmytelegram-blue)](https://github.com/loyldg/mytelegram)

**Testgram** is a fork of [MyTelegram](https://github.com/loyldg/mytelegram) — a self-hosted C# implementation of the Telegram server-side API.

## Supported Features

### Open Source Features
- API Layer: `224`
- MTProto Transports: `Abridged`, `Intermediate`
- Private Chat
- Supergroup Chat
- Channel
- Message Reactions
- Star Gifts (channels, hide/show, unread mentions)
- Passkey Login (WebAuthn)
- Channel Direct Messages (Monoforum)
- Bot Support
- Stories
- Privacy Settings & 2FA
- Voice & Video Calls (WebRTC)
- Telegram Business
- Auto-Delete Messages
- Stickers
- Scheduled Messages
- Forum Topics
- Themes & Wallpapers
- Folders (Dialog Filters)

### Soon...
- End-to-End Encrypted Chat
- Email Login
- Email Sender
- Push Notifications (Firebase)

---

## Running Testgram Server

### Quick Start with Docker

1. Download the Docker Compose files:

```
curl -O https://raw.githubusercontent.com/glebxdlolreal/testgram/dev/docker/compose/docker-compose.yml
curl -O https://raw.githubusercontent.com/glebxdlolreal/testgram/dev/docker/compose/.env.example
cp .env.example .env
```

2. Edit `.env`:
   - Replace `YOUR_SERVER_IP` with your server's public IP address
   - Set strong passwords for `CHANGE_ME` fields (RabbitMQ, Minio, encryption keys)

3. Start the server:

```bash
mkdir -p ./data/mytelegram
chmod -R a+w ./data/mytelegram
docker compose up -d
```

### Configuration

Key `.env` settings:

| Variable | Description |
|----------|-------------|
| `App__DcOptions__0__IpAddress` | Your server's public IP |
| `RabbitMQ__Connections__Default__Password` | RabbitMQ password |
| `App__AccessHashSecretKey` | Random secret key |
| `App__EncryptionConfig__MessageKeys__0__Key` | Base64 encryption key |
| `App__FixedVerifyCode` | Fixed SMS code for testing (leave empty in production) |

### Voice & Video Calls Setup

Voice and video calls **require** a TURN/STUN server. Install Coturn:

```bash
sudo apt-get install coturn
# Configure /etc/turnserver.conf (see docs/CALLS_SETUP.md)
sudo systemctl start coturn
```

Configure WebRTC in `.env`:

```bash
# REQUIRED for calls to work
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123
```

Setup MongoDB indexes (automatic on first start):

```bash
cd scripts && ./setup_call_indexes.sh  # Optional: manual setup
```

See [docs/CALLS_SETUP.md](docs/CALLS_SETUP.md) for complete setup instructions.

## Troubleshooting

### Clients get `ConnectionRefusedError` (connection to server fails)

If clients fail to connect with an error like:

```
Attempt 1 at connecting failed: ConnectionRefusedError: [WinError 1225] The remote computer refused the network connection
```

but the VDS/host itself is reachable, the gateway is most likely not listening on the
main port **20443** (DC1, the first port clients connect to — see `App__DcOptions__0__Port`).

Cause: `App__Servers__0__Enabled` is unset/commented in `.env`. docker-compose always
passes this variable to the gateway container, so an unset value becomes an **empty
string**. An empty value makes .NET drop server 0 from the config entirely, so the
gateway never opens the 20443 listener and every connection is refused.

Fix: make sure `.env` contains an active line (not commented, not empty):

```bash
App__Servers__0__Enabled=True
```

Then recreate the gateway and verify it listens on 20443:

```bash
cd docker/compose
docker compose up -d --force-recreate gateway-server
docker compose logs gateway-server | grep 20443   # expect: "Tcp server started at ...:20443"
```

### file-server spams `Bucket name cannot be empty` / media and verification icons don't load

If `file-server` logs are spammed with:

```
Minio.Exceptions.InvalidBucketNameException: MinIO API responded with message=Bucket name cannot be empty.
```

and avatars, stickers, or custom verification icons fail to load in clients, `Minio__BucketName`
is unset/commented in `.env`. docker-compose always passes this variable to file-server, so an
unset value becomes an empty string, and every file request fails.

Fix: make sure `.env` contains active lines (not commented, not empty):

```bash
Minio__BucketName=tg-files
Minio__CreateBucketIfNotExists=True
```

Then recreate file-server:

```bash
cd docker/compose
docker compose up -d --force-recreate file-server
```

### file-server spams `NullReferenceException` in `MinioStoringHelper.GetAsync` / downloads stall

If `file-server` logs are flooded with:

```
[ERR] Get file failed, input: FileId: "..." Offset: ... Limit: 32768
System.NullReferenceException: Object reference not set to an instance of an object.
   at Minio.MinioClient.ParseWellKnownErrorNoContent(ResponseResult response)
   ...
   at MyTelegram.FileServer.Services.MinioStoringHelper.GetAsync(...)
```

this is a regression in the MinIO .NET SDK bundled inside the upstream
`mytelegram-file-server` image (Minio 6.0.6-local). When MinIO answers a byte-range
request with `416 Range Not Satisfiable` (no body) — which Telegram clients trigger
for the final chunk of a download (offset at/after EOF) — the SDK fails to handle the
416, leaves its error object null, and `throw error;` turns into a NullReferenceException.

Because the file-server is built and published separately, it can't be patched from
this repo. Instead, file-server is routed through the **minio-proxy** service (a tiny
nginx proxy) which rewrites those 416 responses into a clean empty 200 the SDK accepts.
All other traffic passes through untouched.

This is wired up by default (`Minio__FileServerEndpoint` defaults to `minio-proxy:9000`).
If you see this error, make sure the proxy is running and file-server points at it:

```bash
cd docker/compose
docker compose up -d minio-proxy
docker compose up -d --force-recreate file-server
```

## Building Docker Images

```bash
# Linux amd64
cd build/docker && ./build-all-amd64.sh

# Linux arm64
cd build/docker && ./build-all-arm64.sh
```

## Clients

| Platform | Repository |
|----------|------------|
| Android | https://github.com/glebxdlolreal/testgram-android |
| Desktop (TDesktop) | https://github.com/glebxdlolreal/testgram-tdesktop |
| iOS | https://github.com/loyldg/mytelegram-iOS |
| WebK | https://github.com/loyldg/mytelegram-webk |
| WebA | https://github.com/loyldg/mytelegram-weba |

### Configure Clients
1. Clone the client source code.
2. Search for `YOUR_SERVER_IP` in all files and replace it with your own server IP.

## Verification Bot

The repo includes a Telegram bot (`bot/`) that listens for registration codes via RabbitMQ and sends them to users via Telegram.

```bash
cd bot
cp .env.example .env
# Edit .env with your BOT_TOKEN and RABBITMQ_URL
python3 bot.py
```

## Admin: Give Stars to a User

Connect to MongoDB and run:

```js
// mongosh tg

// 1. Add balance
db['star-transactions'].insertOne({
  UserId: Long('USER_ID'),
  Amount: 1000,          // number of stars
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

> Replace `USER_ID` with the target user ID (find it via `db['eventflow-userreadmodel'].find({UserName: 'username'})`).

---

## Admin: Add Star Gifts

Gifts are stored in the `star-gifts` collection. To add a new gift:

```js
// mongosh tg

db['star-gifts'].insertOne({
  GiftId: Long('UNIQUE_GIFT_ID'),   // unique ID (e.g. 1001)
  Stars: 50,                         // price in stars
  Title: 'My Gift',
  Description: '',
  DocumentId: Long('DOCUMENT_ID'),   // sticker/document ID from Telegram
  LimitedQuantity: 0,                // 0 = unlimited
  SoldCount: 0,
  Available: true,
  FirstSaleDate: new Date(),
  LastSaleDate: null
});
```

To give a gift to a user directly (without purchase):

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

## Admin: Add Star Gift Upgrades

To make a gift upgradeable, you need to:

**1. Set upgrade cost on the gift:**
```js
// mongosh tg
db['star-gifts'].updateOne(
  { GiftId: Long('GIFT_ID') },
  { $set: {
    UpgradeStars: 1000,        // stars required to upgrade
    AvailabilityTotal: 10000   // total unique copies that can exist
  }}
);
```

**2. Add upgrade config (attributes for unique version):**

Each unique gift gets 3 attributes: `model` (sticker), `backdrop` (background), `pattern` (overlay).
Add variants to `star-gift-upgrade-config`:

```js
db['star-gift-upgrade-config'].insertMany([
  // Model (sticker variant)
  {
    gift_id: Long('GIFT_ID'),   // 0 = applies to all gifts
    type: 'model',
    name: 'Rare Model',
    rarity_permille: 100,       // 100 = 10% chance (out of 1000)
    document_id: Long('STICKER_DOCUMENT_ID')
  },
  // Backdrop (background colors)
  {
    gift_id: Long('GIFT_ID'),
    type: 'backdrop',
    name: 'Golden',
    rarity_permille: 50,
    backdrop_id: 1,
    center_color: 0xF1C40F,
    edge_color: 0xD4AC0D,
    pattern_color: 0xF9E79F,
    text_color: 0xFFFFFF
  },
  // Pattern (overlay sticker)
  {
    gift_id: Long('GIFT_ID'),
    type: 'pattern',
    name: 'Stars',
    rarity_permille: 200,
    document_id: Long('PATTERN_DOCUMENT_ID')
  }
]);
```

> `rarity_permille` — weight out of 1000 (higher = more common). Use `gift_id: 0` for attributes shared across all gifts.

**3. Force-upgrade a gift for a user (admin):**
```js
// Find the saved gift
db['saved-star-gifts'].findOne({ OwnerUserId: Long('USER_ID'), IsUnique: false });

// Then trigger upgrade via API or set UpgradeStars: 0 to make it free
db['star-gifts'].updateOne(
  { GiftId: Long('GIFT_ID') },
  { $set: { UpgradeStars: 0 } }
);
```

---

## Reaction Seeder

After deploying the server, run the reaction seeder to populate emoji reaction animations:

```bash
cd scripts

# 1. Download reaction files from Telegram (~50MB)
TG_API_ID=your_api_id \
TG_API_HASH=your_api_hash \
TG_PHONE=+1234567890 \
python3 seed_reactions.py --download

# 2. Import files into Minio + MongoDB
MONGO_URL=mongodb://localhost:27017 \
MINIO_ENDPOINT=localhost:9000 \
MINIO_ACCESS_KEY=your_key \
MINIO_SECRET_KEY=your_secret \
python3 seed_reactions.py --import

# 3. Generate the C# handler with real document IDs
MONGO_URL=mongodb://localhost:27017 \
HANDLER_PATH=../source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetAvailableReactionsHandler.cs \
python3 seed_reactions.py --generate-handler

# 4. Rebuild and redeploy messenger images
cd ../build/docker
export REGISTRY_URL="mytelegram"
bash 1.build-messenger-command-server.sh
bash 2.build-messenger-query-server.sh
cd ../../docker/compose && docker compose down && docker compose up -d
```

> **Note:** Steps 1–3 only need to be done once. The generated handler is committed to the repo so subsequent deploys don't require re-seeding.
