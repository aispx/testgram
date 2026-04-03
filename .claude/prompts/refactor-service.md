# Refactor Service Prompt

Use this prompt when refactoring large service classes (God Classes).

## Template

```
Refactor the service class: {ServiceName}

Current problems:
- File has {line_count} lines (too large)
- Multiple responsibilities (violates SRP)
- Hard to test due to tight coupling
- Complex methods with deep nesting

Refactoring strategy:

1. **Analyze responsibilities:**
   - List all public methods
   - Group methods by domain concern
   - Identify shared dependencies

2. **Extract specialized services:**
   - Create focused services with single responsibility
   - Example: MessageAppService → MessageValidationService, MessageSendingService, MessageQueryService
   - Keep original service as facade if needed for backward compatibility

3. **Improve testability:**
   - Extract interfaces for new services
   - Remove direct MongoDB access (use IQueryProcessor/ICommandBus)
   - Make dependencies explicit via constructor injection

4. **Simplify complex methods:**
   - Extract private methods into separate classes
   - Use strategy pattern for conditional logic
   - Apply CQRS: separate read and write operations

5. **Maintain Event Sourcing patterns:**
   - Commands should go through ICommandBus
   - Queries should use IQueryProcessor
   - Don't bypass EventFlow aggregates

## Example Refactoring

### Before (God Class):
```csharp
public class MessageAppService : IMessageAppService
{
    // 851 lines
    // 15+ dependencies
    // Handles validation, sending, querying, encryption, etc.
    
    public async Task<SendMessageResult> SendMessageAsync(...)
    {
        // 100+ lines of mixed concerns
    }
}
```

### After (Focused Services):
```csharp
// 1. Validation service
public class MessageValidationService : IMessageValidationService
{
    public ValidationResult ValidateMessage(string message, Peer peer)
    {
        // Only validation logic
    }
}

// 2. Sending service
public class MessageSendingService : IMessageSendingService
{
    private readonly ICommandBus _commandBus;
    private readonly IMessageValidationService _validation;
    
    public async Task<SendMessageResult> SendAsync(SendMessageRequest request)
    {
        // Only sending logic
        var validationResult = _validation.ValidateMessage(...);
        if (!validationResult.IsValid)
            return SendMessageResult.Failed(validationResult.Errors);
            
        var command = new SendMessageCommand(...);
        await _commandBus.PublishAsync(command, CancellationToken.None);
        
        return SendMessageResult.Success();
    }
}

// 3. Query service
public class MessageQueryService : IMessageQueryService
{
    private readonly IQueryProcessor _queryProcessor;
    
    public async Task<IReadOnlyList<IMessageReadModel>> GetMessagesAsync(...)
    {
        // Only query logic
        return await _queryProcessor.ProcessAsync(
            new GetMessagesQuery(...)
        );
    }
}

// 4. Facade (optional, for backward compatibility)
public class MessageAppService : IMessageAppService
{
    private readonly IMessageSendingService _sending;
    private readonly IMessageQueryService _query;
    
    public MessageAppService(
        IMessageSendingService sending,
        IMessageQueryService query)
    {
        _sending = sending;
        _query = query;
    }
    
    public Task<SendMessageResult> SendMessageAsync(...)
        => _sending.SendAsync(...);
        
    public Task<IReadOnlyList<IMessageReadModel>> GetMessagesAsync(...)
        => _query.GetMessagesAsync(...);
}
```

## Refactoring Checklist

- [ ] Identify all responsibilities in the class
- [ ] Create new focused service classes
- [ ] Extract interfaces for each service
- [ ] Move methods to appropriate services
- [ ] Update dependency injection registration
- [ ] Update all usages of the original service
- [ ] Add unit tests for new services
- [ ] Verify all existing functionality still works
- [ ] Update documentation

## Testing Strategy

After refactoring, create tests for each new service:

```csharp
public class MessageValidationServiceTests
{
    [Fact]
    public void ValidateMessage_WithEmptyMessage_ShouldReturnInvalid()
    {
        // Arrange
        var service = new MessageValidationService();
        
        // Act
        var result = service.ValidateMessage("", new Peer(...));
        
        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Message cannot be empty");
    }
}
```

## Important Notes

1. **Don't break Event Sourcing:**
   - Commands must go through ICommandBus
   - Don't bypass aggregates
   - Keep domain events

2. **Backward compatibility:**
   - Keep original service as facade if needed
   - Update usages gradually
   - Don't break existing handlers

3. **Incremental refactoring:**
   - Start with one responsibility
   - Test thoroughly
   - Move to next responsibility

Now refactor: {ServiceName}
```

## Usage Example

```
Refactor the service class: MessageAppService

Current file: source/src/MyTelegram.Messenger/Services/Impl/MessageAppService.cs
Line count: 851 lines
Problems: Too many responsibilities, hard to test, tight coupling to MongoDB

[Claude will analyze and refactor the service following the template]
```
