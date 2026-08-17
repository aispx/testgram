---
name: check-handler
description: Verify a Telegram API handler implementation follows best practices. Use after implementing or modifying handlers to catch common mistakes.
allowed-tools: Read(**), Grep(*)
argument-hint: <handler-file-path>
---

# Check Handler Implementation

Verify that a handler follows Testgram best practices and catches common mistakes.

## Usage

```bash
/check-handler source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetStickerSetHandler.cs
```

## What This Checks

### 1. Class Declaration
- ✅ Must be `internal sealed class`
- ✅ Must inherit from `RpcResultObjectHandler<TRequest, TResponse>`
- ✅ Correct namespace: `MyTelegram.Messenger.Handlers.LatestLayer.<Category>`

### 2. Security
- ✅ Uses `input.UserId` from token (not from request)
- ❌ Never uses `obj.UserId` or similar client-provided user IDs

### 3. Error Handling
- ✅ Uses `RpcErrors.RpcErrors400.*` for errors
- ❌ Never throws generic `Exception` or `NotImplementedException`

### 4. TL Types
- ✅ All `TVector<T>` fields are initialized (never null)
- ✅ Required fields like `Photo`, `RestrictionReason` are set
- ✅ `Date` fields use `DateTimeOffset.UtcNow.ToUnixTimeSeconds()`

### 5. MongoDB
- ✅ Uses `_database.GetCollection<BsonDocument>("collection")`
- ❌ Never modifies `eventflow-*` collections directly (use read models only)
- ✅ Handles null/missing fields safely

### 6. Performance
- ✅ Batch loads related data (no N+1 queries)
- ✅ Uses indexes for queries

## Check Process

1. **Read the handler file**: `$ARGUMENTS`

2. **Verify class structure**:
   - Check class declaration
   - Check namespace
   - Check inheritance

3. **Check security**:
   - Search for `input.UserId` usage
   - Search for dangerous patterns like `obj.UserId`

4. **Check error handling**:
   - Search for `RpcErrors`
   - Search for generic exceptions

5. **Check TL types**:
   - Search for `TVector` initialization
   - Search for null assignments
   - Check Date field handling

6. **Check MongoDB usage**:
   - Search for collection access
   - Check for eventflow-* modifications
   - Verify safe field access

7. **Report findings**:
   - List all issues found
   - Provide specific line numbers
   - Suggest fixes

## Example Output

```
✅ Class declaration: internal sealed class ✓
✅ Namespace: MyTelegram.Messenger.Handlers.LatestLayer.Messages ✓
✅ Uses input.UserId: Line 40 ✓
✅ Uses RpcErrors: Lines 38, 45 ✓
✅ TVector initialized: Lines 52, 53, 54 ✓
⚠️  Warning: Potential N+1 query at line 67
❌ Error: TVector<IUser> not initialized at line 71
```

## Common Issues Found

### Issue 1: Null TVector
```csharp
// ❌ WRONG
return new TStickerSet { Packs = null };

// ✅ CORRECT
return new TStickerSet { Packs = new TVector<IStickerPack>() };
```

### Issue 2: Using Client UserId
```csharp
// ❌ WRONG
var userId = obj.UserId;

// ✅ CORRECT
var userId = input.UserId;
```

### Issue 3: Generic Exception
```csharp
// ❌ WRONG
throw new Exception("Invalid");

// ✅ CORRECT
RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
```

### Issue 4: N+1 Query
```csharp
// ❌ WRONG
foreach (var id in ids)
    var doc = await collection.Find(f => f["Id"] == id).FirstOrDefaultAsync();

// ✅ CORRECT
var docs = await collection.Find(Builders<BsonDocument>.Filter.In("Id", ids)).ToListAsync();
```

## After Checking

If issues found:
1. Fix the issues in the handler
2. Run `/rebuild-service messenger-command-server`
3. Test with official Telegram client
4. Check logs: `docker compose -p mytelegram logs -f messenger-command-server`
