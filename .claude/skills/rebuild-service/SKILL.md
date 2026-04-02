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

- `messenger-command-server` - Main RPC handler service
- `messenger-query-server` - Read model query service
- `gateway-server` - MTProto gateway
- `session-server` - Session management (closed-source binary)

## What This Does

1. Builds Docker image for the specified service
2. Restarts the container with new image
3. Shows logs to verify startup

## Implementation

```bash
# Navigate to build directory
cd /root/testgram/build/docker

# Run build script for the service
./1.build-$ARGUMENTS.sh

# Navigate to compose directory
cd /root/testgram/docker/compose

# Restart the service
docker-compose up -d $ARGUMENTS

# Show logs
docker-compose logs -f $ARGUMENTS | head -50
```

## After Rebuild

Check that the service started successfully:

```bash
# Check container status
docker-compose ps $ARGUMENTS

# Check logs for errors
docker-compose logs -f $ARGUMENTS | grep -i error

# Check MongoDB connection
docker-compose exec mongodb mongosh tg --eval "db.serverStatus()"
```

## Common Issues

**Build fails:**
- Check .NET SDK version: `dotnet --version` (need 8.0+)
- Clean build: `cd /root/testgram/scripts && ./delete-bin-obj-folders.sh`

**Container won't start:**
- Check logs: `docker-compose logs $ARGUMENTS`
- Check .env configuration
- Verify MongoDB/RabbitMQ are running

**Handler not found after rebuild:**
- Verify handler namespace: `MyTelegram.Messenger.Handlers.LatestLayer.<Category>`
- Check handler is `internal sealed class`
- Rebuild again to ensure changes were included
