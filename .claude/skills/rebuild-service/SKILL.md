---
name: rebuild-service
description: Rebuild and restart a specific Testgram service. Use when code changes need to be deployed to Docker containers.
allowed-tools: Bash(cd *), Bash(./*)
disable-model-invocation: true
argument-hint: <service-name>
---

# Rebuild Testgram Service

Rebuild a specific service and restart its Docker container.

## Usage

```bash
/rebuild-service messenger-command-server
/rebuild-service gateway-server
/rebuild-service messenger-query-server
```

## Available Services

| Service | Build script |
|---------|--------------|
| `messenger-command-server` - Main RPC handler service | `1.build-messenger-command-server.sh` |
| `messenger-query-server` - Read model query service | `2.build-messenger-query-server.sh` |
| `sms-sender` - Login-code delivery | `4.build-sms-sender.sh` |
| `gateway-server` - MTProto gateway | `5.build-gateway-server.sh` |
| `auth-server` - Auth keys and login | `6.build-auth-server.sh` |
| `data-seeder` - Seed data | `7.build-data-seeder.sh` |
| `session-server` - Session management | no build script (closed-source image) |

The script numbering is not sequential per service and skips `3.`, so the prefix has to be looked
up in this table rather than guessed.

## What This Does

1. Builds Docker image for the specified service
2. Restarts the container with new image
3. Shows logs to verify startup

## Implementation

```bash
# Navigate to build directory
cd /root/testgram/build/docker

# Run the build script for the service - the numeric prefix comes from the table above,
# so resolve it by listing the directory instead of assuming "1."
ls *.build-$ARGUMENTS.sh
./$(ls *.build-$ARGUMENTS.sh | head -1)

# Navigate to compose directory
cd /root/testgram/docker/compose

# Restart the service
docker compose -p mytelegram up -d $ARGUMENTS

# Show logs
docker compose -p mytelegram logs -f $ARGUMENTS | head -50
```

## After Rebuild

Check that the service started successfully:

```bash
# Check container status
docker compose -p mytelegram ps $ARGUMENTS

# Check logs for errors
docker compose -p mytelegram logs -f $ARGUMENTS | grep -i error

# Check MongoDB connection
docker compose -p mytelegram exec mongodb mongosh tg --eval "db.serverStatus()"
```

## Common Issues

**Build fails:**
- Check .NET SDK version: `dotnet --version` (need 8.0+)
- Clean build: `cd /root/testgram/scripts && ./delete-bin-obj-folders.sh`

**Container won't start:**
- Check logs: `docker compose -p mytelegram logs $ARGUMENTS`
- Check .env configuration
- Verify MongoDB/RabbitMQ are running

**Handler not found after rebuild:**
- Verify handler namespace: `MyTelegram.Messenger.Handlers.LatestLayer.<Category>`
- Check handler is `internal sealed class`
- Rebuild again to ensure changes were included
