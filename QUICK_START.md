# 🚀 Quick Start: Testgram Improvement Plan

**Created:** 2026-04-03  
**Status:** Ready to implement

---

## 📚 What Was Created

### 1. **CLAUDE_V2.md** - Enhanced Development Guide
- Event Sourcing + CQRS focused
- Command/Query pattern examples
- Best practices for EventFlow
- Common mistakes to avoid
- Architecture diagrams

**Use this as your primary development guide.**

### 2. **Claude Prompts** (`.claude/prompts/`)

#### `create-handler.md`
Template for creating new Telegram API handlers with proper Event Sourcing patterns.

**Usage:**
```
I need to implement messages.getHistory handler.
Use the create-handler.md prompt template.
```

#### `refactor-service.md`
Template for refactoring large service classes (God Classes) into focused services.

**Usage:**
```
Refactor the service class: MessageAppService
Use the refactor-service.md prompt template.
```

### 3. **IMPROVEMENT_ROADMAP.md** - 10-Week Plan
Detailed roadmap with 5 priorities:
1. Refactor God Classes (2-3 weeks) 🔥🔥🔥
2. Implement Repository Pattern (3-4 weeks) 🔥🔥🔥
3. Fix Exception Handling (1-2 weeks) 🔥🔥
4. Add MongoDB Indexes (1 week) 🔥🔥
5. Add Monitoring (1-2 weeks) 🔥

---

## 🎯 Start Here (This Week)

### Day 1: Add MongoDB Indexes

```bash
# Connect to MongoDB
docker-compose exec mongodb mongosh tg

# Add critical indexes
db["eventflow-userreadmodel"].createIndex({ "UserId": 1 }, { unique: true })
db["eventflow-userreadmodel"].createIndex({ "Usernames.Username": 1 })
db["eventflow-messagereadmodel"].createIndex({ "MessageId": 1 }, { unique: true })
db["eventflow-channelreadmodel"].createIndex({ "ChannelId": 1 }, { unique: true })
db.fragment_collectibles.createIndex({ "username": 1 })
```

### Day 2-3: Analyze God Classes

```
Analyze MessageAppService.cs and list all its responsibilities.

Group methods by domain concern.

File: source/src/MyTelegram.Messenger/Services/Impl/MessageAppService.cs
```

### Day 4-5: Refactor One Service

Pick a simple service to refactor as proof-of-concept:

```
Refactor the service class: MessageAppService

Extract validation logic from MessageAppService into MessageValidationService.

Use the refactor-service.md prompt template.
```

---

## 📖 How to Use Claude Prompts

### Method 1: Direct Reference
```
I need to create a new handler for messages.editMessage.

Use the prompt template from .claude/prompts/create-handler.md
```

### Method 2: Copy-Paste Template
1. Open `.claude/prompts/create-handler.md`
2. Copy the template
3. Replace `{method_name}` with your method
4. Send to Claude

### Method 3: Custom Prompt
```
Create a handler for messages.editMessage following these rules:
- Use ICommandBus for write operations
- Use IQueryProcessor for read operations
- Validate all inputs
- Use RpcErrors for errors
- Initialize all TVector fields

[Claude will follow the patterns from CLAUDE_V2.md]
```

---

## 🔄 Refactoring Workflow

### 1. Identify God Class
```bash
# Find large files
find source/src -name "*.cs" -exec wc -l {} + | sort -rn | head -20
```

### 2. Analyze Responsibilities
```
Analyze MessageAppService.cs and list all its responsibilities.

Group methods by domain concern.
```

### 3. Extract Services
```
Refactor MessageAppService by extracting:
1. MessageValidationService - validation logic
2. MessageSendingService - sending logic
3. MessageQueryService - query logic

Keep MessageAppService as facade for backward compatibility.

Use the refactor-service.md prompt template.
```

### 4. Migrate Usages
```
Update all handlers that use MessageAppService to use the new focused services.

Maintain backward compatibility.
```

---

## 📊 Progress Tracking

### Week 1-2 Goals
- [ ] MongoDB indexes added
- [ ] 1 service refactored (MessageAppService)
- [ ] Repository interfaces created

### Week 3-4 Goals
- [ ] Repository implementations done
- [ ] Exception handling improved
- [ ] First handlers migrated to repositories

### Week 5-6 Goals
- [ ] All handlers migrated to repositories
- [ ] No BsonDocument usage in handlers
- [ ] Monitoring setup started

---

## 🎓 Learning Resources

### Event Sourcing
- [EventFlow Documentation](https://github.com/eventflow/EventFlow)
- [CQRS Journey by Microsoft](https://docs.microsoft.com/en-us/previous-versions/msp-n-p/jj554200(v=pandp.10))

### Telegram API
- [Official API Docs](https://core.telegram.org/api)
- [TL Schema](https://core.telegram.org/schema)
- [Android Client Source](https://github.com/DrKLO/Telegram)

---

## 💡 Tips for Success

1. **Start Small**
   - Don't try to refactor everything at once
   - Pick one service, refactor it, deploy it
   - Learn from each iteration

2. **Use Claude Effectively**
   - Be specific in your requests
   - Provide context (file paths, line numbers)
   - Review generated code carefully
   - Iterate on the output

3. **Maintain Backward Compatibility**
   - Use facade pattern when refactoring
   - Don't break existing handlers
   - Migrate gradually

4. **Document Decisions**
   - Update CLAUDE_V2.md with new patterns
   - Add comments for complex logic
   - Create ADRs for architectural decisions

---

## 🚨 Common Pitfalls to Avoid

1. **Don't bypass Event Sourcing**
   - Always use ICommandBus for writes
   - Never write directly to MongoDB
   - Respect aggregate boundaries

2. **Don't query aggregates**
   - Aggregates are for writes only
   - Use read models for queries
   - Use IQueryProcessor

3. **Don't create big PRs**
   - Small, focused PRs
   - Easy to review
   - Easy to rollback

4. **Don't ignore auto-generated files**
   - *.g.cs files are generated
   - Don't edit them manually
   - Improve the generator instead

---

## 📞 Need Help?

### Ask Claude
```
I'm stuck on [problem].

Context:
- File: [file path]
- What I'm trying to do: [description]
- What's happening: [error/issue]
- What I've tried: [attempts]

Help me fix this.
```

### Check Documentation
1. CLAUDE_V2.md - Development guide
2. IMPROVEMENT_ROADMAP.md - Detailed plan
3. `.claude/prompts/` - Prompt templates

### Review Examples
- Look at existing handlers for patterns
- Study domain aggregates for Event Sourcing patterns

---

## ✅ Success Metrics

After 10 weeks, you should have:

- **Zero God Classes** (all < 500 lines)
- **Repository pattern** implemented
- **Zero generic exception catching**
- **All collections indexed**
- **Monitoring in place**

---

**Ready to start? Begin with Day 1 tasks above! 🚀**

**Questions? Use Claude with the prompts in `.claude/prompts/`**
