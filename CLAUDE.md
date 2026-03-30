# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Testgram is a self-hosted C# implementation of the Telegram server-side API, forked from MyTelegram. It implements MTProto 2.0 protocol and supports API Layer 222.

## Architecture

### Core Components

**Domain Layer (Event Sourcing + CQRS)**
- `MyTelegram.Domain`: Aggregates using EventFlow framework for event sourcing
- `MyTelegram.Domain.Shared`: Shared domain models and RPC error definitions
- `MyTelegram.EventFlow`: Custom EventFlow extensions and base classes
- Aggregates emit domain events (e.g., `UserAboutUpdatedEvent`) which are persisted and replayed

**Application Layer**
- `MyTelegram.Messenger`: Core business logic and RPC request handlers
  - Handlers organized by layer: `Handlers/LatestLayer/` (Layer 222) and `Handlers/LayerN/` (older layers)
  - Each Telegram RPC method has a corresponding handler (e.g., `CreateBusinessChatLinkHandler`)
- `MyTelegram.Converters`: Maps domain models to Telegram schema types
- `MyTelegram.Services`: Business services and utilities

**Infrastructure**
- `MyTelegram.MTProto`: MTProto protocol implementation (Abridged, Intermediate transports)
- `MyTelegram.Schema`: Auto-generated Telegram TL schema types
- `MyTelegram.EventBus.RabbitMQ`: Event bus implementation using RabbitMQ
- `MyTelegram.Caching.Redis`: Redis-based caching layer

**Read Model (CQRS Query Side)**
- `MyTelegram.ReadModel`: Read model interfaces and base classes
- `MyTelegram.ReadModel.MongoDB`: MongoDB-based read model implementation
- `MyTelegram.QueryHandlers.MongoDB`: Query handlers for read operations
- `MyTelegram.Queries`: Query definitions

**Server Applications**
- `MyTelegram.GatewayServer`: MTProto gateway handling client connections
- `MyTelegram.AuthServer`: Authentication and authorization
- `MyTelegram.Messenger.CommandServer`: Processes write commands (CQRS command side)
- `MyTelegram.Messenger.QueryServer`: Handles read queries (CQRS query side)
- `MyTelegram.DataSeeder`: Seeds initial data into the database
- `MyTelegram.SmsSender`: Sends verification codes

### Data Flow

1. Client connects to `GatewayServer` via MTProto
2. Gateway routes requests to `AuthServer` or `Messenger.CommandServer`/`Messenger.QueryServer`
3. Command handlers emit domain events → EventFlow persists to MongoDB event store
4. Events published to RabbitMQ → Read model projections update MongoDB collections
5. Query handlers read from MongoDB read models

## Building and Running

### Build All Projects

```bash
cd /root/testgram/build
./build.sh
```

This builds all server components to `../out/local/<version>/`.

### Build Docker Images

```bash
cd /root/testgram/build/docker

# Linux amd64
./build-all-amd64.sh

# Linux arm64
./build-all-arm64.sh

# Both platforms
./build-local-all-amd64-arm64.sh
```

Individual services:
```bash
export REGISTRY_URL="mytelegram"  # or your registry
./1.build-messenger-command-server.sh
./2.build-messenger-query-server.sh
./4.build-sms-sender.sh
./5.build-gateway-server.sh
./6.build-auth-server.sh
./7.build-data-seeder.sh
```

### Run with Docker Compose

```bash
cd /root/testgram/docker/compose
docker compose up -d
```

Dependencies: MongoDB, Redis, RabbitMQ, Minio (S3-compatible storage)

### Run Tests

```bash
cd /root/testgram/source
dotnet test MyTelegram.slnx
```

Individual test projects:
```bash
dotnet test test/MyTelegram.Domain.Tests/MyTelegram.Domain.Tests.csproj
dotnet test test/MyTelegram.MTProto.Tests/MyTelegram.MTProto.Tests.csproj
dotnet test test/MyTelegram.Schema.Tests/MyTelegram.Schema.Tests.csproj
dotnet test test/MyTelegram.Services.Tests/MyTelegram.Services.Tests.csproj
```

## Development Workflow

### Adding a New RPC Handler

1. Locate the appropriate layer directory: `source/src/MyTelegram.Messenger/Handlers/LatestLayer/`
2. Create handler inheriting from `RpcResultObjectHandler<TRequest, TResponse>`
3. Implement `HandleCoreAsync` method
4. Handler is auto-registered via dependency injection

Example structure:
```csharp
internal sealed class MyHandler : RpcResultObjectHandler<RequestMyMethod, IMyResponse>
{
    protected override async Task<IMyResponse> HandleCoreAsync(IRequestInput input, RequestMyMethod obj)
    {
        // Implementation
    }
}
```

### Working with Aggregates

Aggregates are in `source/src/MyTelegram.Domain/Aggregates/`. They use EventFlow pattern:
- Aggregates have internal state classes
- Commands trigger methods that emit events
- Events are applied to update state
- Use `Specs.AggregateIsCreated.ThrowDomainErrorIfNotSatisfied(this)` to validate state

### RPC Error Handling

RPC errors defined in `source/src/MyTelegram.Domain.Shared/RpcErrors.g.cs`. Throw errors using:
```csharp
RpcErrors.RpcErrors400.ChatLinksTooMuch.ThrowRpcError();
```

### Database Access

**Command Side**: Use aggregates and domain events (EventFlow handles persistence)

**Query Side**: Direct MongoDB access via `IMongoDatabase` or query handlers in `MyTelegram.QueryHandlers.MongoDB`

**Collections**: MongoDB collections follow naming convention (e.g., `businesschatlinks`, `eventflow-userreadmodel`, `star-gifts`)

## Scripts and Tools

### Reaction Seeder

Populates emoji reaction animations from Telegram:

```bash
cd /root/testgram/scripts

# 1. Download reactions from Telegram
TG_API_ID=your_api_id TG_API_HASH=your_api_hash TG_PHONE=+1234567890 \
python3 seed_reactions.py --download

# 2. Import to Minio + MongoDB
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

### Verification Bot

Python bot in `bot/` directory sends verification codes via Telegram (listens to RabbitMQ):

```bash
cd /root/testgram/bot
cp .env.example .env
# Edit .env with BOT_TOKEN and RABBITMQ_URL
python3 bot.py
```

## Configuration

Main configuration via environment variables in `docker/compose/.env`:

- `App__DcOptions__0__IpAddress`: Server public IP
- `RabbitMQ__Connections__Default__Password`: RabbitMQ password
- `App__AccessHashSecretKey`: Secret key for access hash generation
- `App__EncryptionConfig__MessageKeys__0__Key`: Base64 encryption key
- `App__FixedVerifyCode`: Fixed SMS code for testing (empty in production)

## Project Structure

```
source/src/
├── Application/          # Application services and handlers
│   ├── MyTelegram.Messenger/           # Core RPC handlers
│   ├── MyTelegram.Messenger.CommandServer/  # Command server entry point
│   ├── MyTelegram.Messenger.QueryServer/    # Query server entry point
│   ├── MyTelegram.GatewayServer/       # MTProto gateway
│   ├── MyTelegram.AuthServer/          # Auth service
│   └── MyTelegram.DataSeeder/          # Database seeder
├── Domain/              # Domain layer (event sourcing)
│   ├── MyTelegram.Domain/              # Aggregates and domain logic
│   ├── MyTelegram.Domain.Shared/       # Shared domain models
│   └── MyTelegram.EventFlow/           # EventFlow extensions
├── Infrastructure/      # Infrastructure concerns
│   ├── MyTelegram.MTProto/             # MTProto protocol
│   ├── MyTelegram.Schema/              # Telegram TL schema
│   ├── MyTelegram.EventBus.RabbitMQ/   # RabbitMQ integration
│   └── MyTelegram.Caching.Redis/       # Redis caching
└── ReadModel/           # CQRS read side
    ├── MyTelegram.ReadModel.MongoDB/   # MongoDB read models
    └── MyTelegram.QueryHandlers.MongoDB/  # Query handlers
```

## Common Tasks

**Clean build artifacts:**
```bash
./scripts/delete-bin-obj-folders.sh
```

**View logs:**
```bash
cd /root/testgram/docker/compose
docker compose logs -f messenger-command-server
docker compose logs -f messenger-query-server
```

**Access MongoDB:**
```bash
docker compose exec mongodb mongosh tg
```

**Restart services after code changes:**
```bash
cd /root/testgram/build/docker
./1.build-messenger-command-server.sh
./2.build-messenger-query-server.sh
cd ../../docker/compose
docker compose down
docker compose up -d
```
