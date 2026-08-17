---
name: test-handler
description: Test a Telegram API handler by checking logs and MongoDB data. Use after implementing a handler to verify it works correctly.
allowed-tools: Bash(docker compose *), Bash(docker *), Read(**)
disable-model-invocation: true
argument-hint: <handler-name>
---

# Test Handler Implementation

Test a handler by checking Docker logs, MongoDB data, and verifying the response.

## Usage

```bash
/test-handler GetStickerSetHandler
/test-handler CreateStickerSetHandler
```

## Test Process

### 1. Check if handler is registered
```bash
# Check logs for handler initialization
docker compose -p mytelegram logs messenger-command-server | grep -i "$ARGUMENTS"
```

### 2. Check recent requests
```bash
# Show recent logs (last 100 lines)
docker compose -p mytelegram logs --tail=100 messenger-command-server | grep -i "handler"
```

### 3. Check MongoDB data
```bash
# Connect to MongoDB and check relevant collections
docker compose -p mytelegram exec mongodb mongosh tg --eval "
  print('=== Sticker Sets ===');
  db['eventflow-stickersetreadmodel'].find().limit(5).forEach(printjson);
  
  print('\\n=== Documents ===');
  db['eventflow-documentreadmodel'].find().limit(5).forEach(printjson);
"
```

### 4. Check for errors
```bash
# Search for errors in logs
docker compose -p mytelegram logs --tail=200 messenger-command-server | grep -i error
docker compose -p mytelegram logs --tail=200 messenger-command-server | grep -i exception
```

### 5. Verify handler file exists
```bash
# Find handler file
find /root/testgram/source/src/MyTelegram.Messenger/Handlers/LatestLayer -name "*$ARGUMENTS*"
```

## What to Look For

### ✅ Good Signs
- Handler appears in startup logs
- No exceptions in logs
- MongoDB collections have data
- Response matches TL schema

### ❌ Bad Signs
- Handler not found in logs
- NullReferenceException
- Empty MongoDB collections
- Client crashes on response

## Common Issues

### Handler Not Called
**Symptoms:**
- No logs when calling the method
- Client shows "Method not found"

**Checks:**
```bash
# 1. Verify handler class
grep -n "internal sealed class.*$ARGUMENTS" source/src/MyTelegram.Messenger/Handlers/LatestLayer/**/*.cs

# 2. Check namespace
grep -n "namespace MyTelegram.Messenger.Handlers.LatestLayer" source/src/MyTelegram.Messenger/Handlers/LatestLayer/**/*$ARGUMENTS*.cs

# 3. Rebuild
cd /root/testgram/build/docker && ./1.build-messenger-command-server.sh
```

### Handler Returns Empty Response
**Symptoms:**
- Client receives empty or null response
- No error in logs

**Checks:**
```bash
# Check for TVector initialization
grep -n "TVector" source/src/MyTelegram.Messenger/Handlers/LatestLayer/**/*$ARGUMENTS*.cs

# Check MongoDB query
docker compose -p mytelegram logs --tail=50 messenger-command-server | grep -i "mongodb"
```

### Client Crashes
**Symptoms:**
- Client crashes when receiving response
- "Unsupported constructorId" error

**Checks:**
```bash
# 1. Check constructor IDs
/schema-jppgr-am search $ARGUMENTS

# 2. Verify TL schema matches
grep -n "ConstructorId\|0x" source/src/MyTelegram.Schema/**/*.cs | grep -i "$ARGUMENTS"
```

## Manual Testing Steps

1. **Open official Telegram client**
2. **Trigger the handler** (e.g., open sticker picker for GetStickerSet)
3. **Watch logs in real-time**:
   ```bash
   docker compose -p mytelegram logs -f messenger-command-server
   ```
4. **Check MongoDB after operation**:
   ```bash
   docker compose -p mytelegram exec mongodb mongosh tg
   db.getCollectionNames()
   db["eventflow-stickersetreadmodel"].find().limit(5)
   ```

## Success Criteria

- ✅ Handler appears in logs
- ✅ No exceptions thrown
- ✅ MongoDB data is correct
- ✅ Client receives valid response
- ✅ Client doesn't crash

## Next Steps

If tests pass:
- ✅ Handler is working correctly
- Consider adding more test cases

If tests fail:
- Read error messages carefully
- Check MongoDB data structure
- Verify TL schema compatibility
- Use `/check-handler` to find code issues
