# Create New Handler Prompt

Use this prompt when creating a new Telegram API handler.

## Template

```
Create a new handler for Telegram API method: {method_name}

Requirements:
1. Research phase:
   - Check TL schema: /schema.jppgr.am search {method_name}
   - Read official docs: https://core.telegram.org/method/{method_name}
   - Check Android client implementation

2. Handler structure:
   - Namespace: MyTelegram.Messenger.Handlers.LatestLayer.{Category}
   - Class: internal sealed class {MethodName}Handler
   - Inherit: RpcResultObjectHandler<Request{MethodName}, {ResponseType}>
   - Add XML doc comment with link to official docs

3. Implementation rules:
   - Use input.UserId from auth token (NEVER from request)
   - Validate all inputs before processing
   - Use RpcErrors for error responses
   - Initialize all TVector<T> fields (never null)
   - Use IQueryProcessor for read operations
   - Use ICommandBus for write operations (Event Sourcing)
   - Avoid direct MongoDB access if possible

4. Error handling:
   - Use specific RpcErrors (not generic exceptions)
   - Handle all edge cases from official docs
   - Add proper validation

5. Testing:
   - Test with official Telegram client
   - Verify MongoDB data after operation
   - Check logs for errors

Example handler structure:

```csharp
namespace MyTelegram.Messenger.Handlers.LatestLayer.{Category};

/// <summary>
/// {Description from official docs}
/// See https://core.telegram.org/method/{method_name}
/// </summary>
internal sealed class {MethodName}Handler : RpcResultObjectHandler<Request{MethodName}, {ResponseType}>
{
    private readonly IQueryProcessor _queryProcessor;
    private readonly ICommandBus _commandBus;
    
    public {MethodName}Handler(
        IQueryProcessor queryProcessor,
        ICommandBus commandBus)
    {
        _queryProcessor = queryProcessor;
        _commandBus = commandBus;
    }
    
    protected override async Task<{ResponseType}> HandleCoreAsync(
        IRequestInput input,
        Request{MethodName} obj)
    {
        // 1. Validate input
        if (/* validation */)
            RpcErrors.RpcErrors400.{ErrorType}.ThrowRpcError();
        
        // 2. Get userId from token
        var userId = input.UserId;
        
        // 3. Query read model (if needed)
        var readModel = await _queryProcessor.ProcessAsync(
            new Get{Entity}Query(/* params */)
        );
        
        // 4. Execute command (if write operation)
        var command = new {Command}(/* params */);
        await _commandBus.PublishAsync(command, CancellationToken.None);
        
        // 5. Build response
        return new T{ResponseType}
        {
            // Initialize all fields
            // Never leave TVector<T> as null
        };
    }
}
```

Now create the handler for: {method_name}
```

## Usage Example

```
Create a new handler for Telegram API method: messages.getHistory

[Claude will research, implement, and test the handler following the template]
```

## Common Mistakes to Avoid

1. ❌ Using `obj.UserId` instead of `input.UserId`
2. ❌ Leaving `TVector<T>` fields as null
3. ❌ Using generic `throw new Exception()` instead of `RpcErrors`
4. ❌ Direct MongoDB access with `BsonDocument` (use Query/Command pattern)
5. ❌ Missing input validation
6. ❌ Not initializing required TL fields

## After Creating Handler

1. Build: `cd build/docker && ./1.build-messenger-command-server.sh`
2. Restart: `docker-compose restart messenger-command-server`
3. Test with official Telegram client
4. Check logs: `docker-compose logs -f messenger-command-server`
5. Verify data: `docker-compose exec mongodb mongosh tg`
