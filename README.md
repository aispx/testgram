# Testgram

[![API Layer](https://img.shields.io/badge/API_Layer-224-blueviolet)](https://corefork.telegram.org/methods)
[![MTProto](https://img.shields.io/badge/MTProto_Protocol-2.0-green)](https://corefork.telegram.org/mtproto/)
[![Fork](https://img.shields.io/badge/fork-loyldg%2Fmytelegram-blue)](https://github.com/loyldg/mytelegram)
[![Testgram Channel](https://img.shields.io/badge/Subscribe-_Testgram_Channel-0088cc)](https://t.me/testgramrofl)
[![Testgram Discussion Group](https://img.shields.io/badge/Join_-Testgram_Discussion_Group-0088cc)](https://t.me/+etFTfnAPU7Q1M2Ri)

**Testgram** is a fork of [MyTelegram](https://github.com/loyldg/mytelegram) — a self-hosted C# implementation of the Telegram server-side API.

## Supported Features

### Open Source Features
- API Layer: `224`
- MTProto Transports: `Abridged`, `Intermediate`
- Private Chat
- Supergroup Chat
- Channel
- Message Reactions
- End-to-End Encrypted Chat (Secret Chats)
- Star Gifts (channels, hide/show, unread mentions, auctions, collections, crafting)
- Star Gift Upgrades & Unique Gifts (NFT themes)
- Resale of Star Gifts in TON / Stars
- Passkey Login (WebAuthn)
- Channel Direct Messages (Monoforum / paid messages)
- Bot Support (incl. BotFather, business bots, affiliate / Star Ref bots)
- Stories (incl. albums, live streams)
- Privacy Settings & 2FA
- Voice & Video Calls (1:1, WebRTC)
- Group Calls / Voice & Video Chats
- Conference Calls (E2E encrypted)
- Live Streams (RTMP / HLS)
- Push Notifications (APNS / FCM / WebPush)
- Email Sender, Email Verification & 2FA Recovery Email
- Telegram Business
- Auto-Delete Messages
- Stickers (incl. sticker sets, custom emoji)
- Scheduled Messages
- Forum Topics
- Themes & Wallpapers
- Folders (Dialog Filters) & Shared Folders (chatlists)
- Saved Music
- Channel Statistics (stats.*)
- Admin Logs
- Todo Lists (collaborative checklists)
- Fact Check
- Giveaways & Boosts
- Multiple Usernames (incl. Fragment NFT usernames)
- Language Packs (incl. Russian)

### Soon...
- Full Email Login (email verification flow is implemented; logging in directly with an email address is not yet supported)

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

> **ARM hosts only:** if you are deploying on an ARM architecture (Apple Silicon,
> Raspberry Pi, AWS Graviton, etc.), add `DOCKER_PLATFORM=linux/arm64` to `.env`
> **before** the first `docker compose up`. Otherwise the bundled third-party images
> (redis, rabbitmq, mongodb, minio, coturn, mediamtx, ...) run through linux/amd64
> emulation and are noticeably slower. On regular x86_64 servers nothing is needed —
> `linux/amd64` is the default.

### Configuration

Key `.env` settings:

| Variable | Description |
|----------|-------------|
| `DOCKER_PLATFORM` | Architecture for third-party images; `linux/amd64` (default) or `linux/arm64` for ARM hosts |
| `App__DcOptions__0__IpAddress` | Your server's public IP |
| `RabbitMQ__Connections__Default__Password` | RabbitMQ password |
| `App__AccessHashSecretKey` | Random secret key |
| `App__EncryptionConfig__MessageKeys__0__Key` | Base64 encryption key |
| `App__FixedVerifyCode` | Fixed SMS code for testing (leave empty in production) |
| `App__PasskeyRpId` | Relying Party ID for Passkey (WebAuthn) login |
| `App__EnableEmailLogin` | Enable email verification flow during login |
| `App__Stripe__SecretKey` | Stripe secret key (for paid star gift purchases) |
| `EmailSenderOptions__SmtpEmailOptions__*` | SMTP settings for email verification & 2FA recovery |

### Voice & Video Calls Setup

Calls **require** a TURN/STUN server. As of the latest version this is **bundled**:
the `docker compose` stack ships a `coturn` service (STUN/TURN) and a `rtmp-server`
service (`mediamtx`, used for group-call live streams). You no longer need to install
Coturn on the host — just point the WebRTC config at your server IP.

Configure WebRTC in `.env` (the credentials **must match** the bundled coturn user,
which defaults to `testgram:testgram2024`):

```bash
# REQUIRED for calls to work
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram2024
```

Group calls and live streams use the bundled RTMP/HLS server:

```bash
App__RtmpStreamUrl=rtmp://YOUR_SERVER_IP:1935/live
App__RtmpHlsUrl=http://rtmp-server:8888/live
RTMP_PORT=1935
RTMP_HLS_PORT=8888
```

MongoDB call indexes are created **automatically** on first start via the `call-init`
container. To run them manually:

```bash
cd scripts && ./setup_call_indexes.sh  # Optional: manual setup
```

Open the required UDP/TCP ports on your firewall: `3478` (STUN/TURN), `49152-49172/udp`
(TURN relay), and `1935` (RTMP). See [docs/CALLS_SETUP.md](docs/CALLS_SETUP.md) for
complete setup instructions, including using an external TURN server.

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

The repo includes a Telegram bot (`bot/`) that listens for registration codes via RabbitMQ and sends them to users via Telegram. It supports running multiple bots (`BOT_TOKEN`, `BOT_TOKEN1`, `BOT_TOKEN2`, ...) and an optional SOCKS5/HTTP proxy.

```bash
cd bot
cp .env.example .env
# Edit .env with your BOT_TOKEN(s) and RABBITMQ_URL
python3 bot.py
```

## Admin: Give Stars to a User

The easiest way is the bundled helper script:

```bash
cd scripts && ./give-stars.sh <user_id> <stars_amount>
# Example: ./give-stars.sh 2010001 1000
```

This updates the user's balance in the `star-balances` collection and records a
transaction in `star-transactions`. To do it manually via `mongosh tg`:

```js
// 1. Add balance
db['star-balances'].updateOne(
  { UserId: Long('USER_ID') },
  { $inc: { Balance: 1000 } },
  { upsert: true }
);

// 2. Record transaction
db['star-transactions'].insertOne({
  _id: ObjectId().toString(),
  TransactionId: ObjectId().toString(),
  UserId: Long('USER_ID'),
  Amount: 1000,          // number of stars
  Gift: false,
  Refund: false,
  Date: Math.floor(Date.now() / 1000),
  Title: 'Admin top-up',
  PeerUserId: null,
  PeerChannelId: null
});
```

> Replace `USER_ID` with the target user ID (find it via `db['eventflow-userreadmodel'].find({UserName: 'username'})`).

---

## Admin: Add Star Gifts

Gifts are stored in the `star-gifts` collection. Use the bundled seeder to create
a full set of sample gifts plus upgrade variants:

```bash
cd scripts && ./init-star-gifts.sh
```

To add a single gift manually (`mongosh tg`):

```js
db['star-gifts'].insertOne({
  GiftId: Long('UNIQUE_GIFT_ID'),   // unique ID (e.g. 1001)
  Stars: 50,                         // price in stars
  ConvertStars: 40,                  // stars you get when converting
  UpgradeStars: null,                // null/0 = not upgradeable
  Limited: false,                    // true = limited supply
  SoldOut: false,
  Birthday: false,
  RequirePremium: false,
  LimitedPerUser: false,
  AvailabilityTotal: null,           // total copies (for limited gifts)
  AvailabilityRemains: null,
  Title: 'My Gift',
  DocumentId: Long('DOCUMENT_ID'),   // sticker/document ID from Telegram
  DocumentAccessHash: Long('ACCESS_HASH'),
  DocumentDate: Math.floor(Date.now() / 1000),
  MimeType: 'application/x-tgsticker',
  DocumentSize: Long('50000'),
  DcId: 2,
  IsAuction: false,
  RandomId: Long('1')
});
```

To give a gift to a user directly (without purchase):

```js
db['saved-star-gifts'].insertOne({
  OwnerUserId: Long('RECIPIENT_USER_ID'),  // recipient
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

Each unique gift gets 3 attribute types: `model` (sticker), `backdrop` (background), `pattern` (overlay).
The bundled seeder `scripts/init-star-gifts.sh` inserts a full set of variants into
`star-gift-upgrade-config` (4 models, 4 backdrops, 4 patterns). To add variants
manually (`mongosh tg`):

```js
db['star-gift-upgrade-config'].insertMany([
  // Model (sticker variant)
  {
    Type: 'model',
    Name: 'Rare Model',
    RarityPermille: 100,        // 100 = 10% chance (out of 1000)
    DocumentId: Long('STICKER_DOCUMENT_ID'),
    DocumentAccessHash: Long('ACCESS_HASH'),
    DocumentDate: Math.floor(Date.now() / 1000),
    MimeType: 'application/x-tgsticker',
    DocumentSize: Long('50000'),
    DcId: 2
  },
  // Backdrop (background colors)
  {
    Type: 'backdrop',
    Name: 'Golden',
    RarityPermille: 50,
    BackdropId: 1,
    CenterColor: 0xF1C40F,
    EdgeColor: 0xD4AC0D,
    PatternColor: 0xF9E79F,
    TextColor: 0xFFFFFF
  },
  // Pattern (overlay sticker)
  {
    Type: 'pattern',
    Name: 'Stars',
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

> `RarityPermille` — weight out of 1000 (higher = more common).

**3. Release a theme for a unique gift (admin):**
```bash
cd scripts && ./release-gift-theme.sh <gift_id> <center_color> [edge_color] [pattern_color] [text_color]
# Example: ./release-gift-theme.sh 900 0x3390ec 0x6fb1f6 0x8ac5f8 0xffffff
```

The theme is owned by the **gift type** (`star-gifts` document by `GiftId`), not
by a single NFT. Passing the `GiftId` of the star gift releases the theme for
**all** existing NFTs of that type, and every future collectible (freshly
upgraded or transferred) inherits the theme automatically. Existing NFTs and
saved gifts are also stamped for immediate pickup by already-sent gift messages.

---

## Admin: Fragment NFT Usernames

The server supports multiple usernames per user/channel, including collectible
(Fragment NFT) usernames. To seed sample collectibles:

```bash
cd scripts && ./init-fragment-nft.sh
```

To assign an NFT username to a user (must exist in `fragment_collectibles`):

```bash
cd scripts && ./assign-nft-username.sh <user_id> <nft_username>
# Example: ./assign-nft-username.sh 2010001 blockchain
```

To add a collectible manually (`mongosh tg`):

```js
db.fragment_collectibles.insertOne({
  _id: 'fragment-username-myusername',
  type: 'username',             // "username" or "phone"
  username: 'myusername',
  phone: null,                  // required if type="phone"
  purchase_date: Math.floor(Date.now() / 1000),
  currency: 'USD',
  amount: 14500,                // 145.00 USD
  crypto_currency: 'TON',
  crypto_amount: NumberLong('50000000000'),
  url: 'https://fragment.com/username/myusername'
});
```

NFT usernames are activated/deactivated via `account.toggleUsername`,
`channels.toggleUsername` and `bots.toggleUsername`; the primary username is
controlled by `account.reorderUsernames` / `channels.reorderUsernames`.

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

## Support Testgram

If you find Testgram helpful, please consider giving the project a ⭐️.

## Feedback

- Contact author: https://t.me/glebxdlol
- Testgram Channel: https://t.me/testgramrofl
- Discussion Group: https://t.me/+etFTfnAPU7Q1M2Ri
