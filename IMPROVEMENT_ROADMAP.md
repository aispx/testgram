# Testgram Improvement Roadmap

**Project:** Testgram (MyTelegram fork)  
**Date:** 2026-04-03  
**Timeframe:** 2-3 months  

---

## 📊 Current State Assessment

### Strengths ✅
- Solid architecture foundation (CQRS + Event Sourcing with EventFlow)
- Clean layer separation (Domain, Application, Infrastructure)
- Modern tech stack (.NET 10, MongoDB, RabbitMQ, Redis)
- Good documentation (CLAUDE.md)
- 792 handlers covering most Telegram API

### Critical Issues 🔴
- **God Classes:** Files with 12K+ lines (mostly auto-generated)
- **233 files with generic exception catching**
- **Tight coupling:** Handlers directly access MongoDB with BsonDocument
- **No Repository pattern:** Makes refactoring difficult

### Metrics
- **Total C# files:** 5,645
- **Projects:** 33
- **Handlers:** 792
- **Technical debt:** High

---

## 🎯 Top 5 Priorities (Next 2-3 Months)

### Priority 1: Refactor God Classes 🔥🔥🔥
**Why Critical:**
- Impossible to understand and maintain
- Violates Single Responsibility Principle
- Slows down development

**Effort:** 2-3 weeks

**Impact:** 🔥🔥🔥 (Highest)

**Target Files:**

1. **MessageAppService.cs (851 lines)**
   ```
   Extract to:
   - MessageValidationService
   - MessageSendingService
   - MessageQueryService
   - MessageEncryptionService
   
   Keep MessageAppService as facade for backward compatibility
   ```

2. **CountryHelper.cs (3,443 lines)**
   ```
   Extract to:
   - CountryDataProvider (data only)
   - CountryLookupService (lookup logic)
   - CountryValidationService (validation)
   ```

3. **TimezoneHelper.cs (2,143 lines)**
   ```
   Extract to:
   - TimezoneDataProvider
   - TimezoneLookupService
   ```

**Note:** Don't refactor auto-generated files (*.g.cs) - improve generator instead

**Claude Automation:**
```bash
# Use refactor-service.md prompt
"Refactor the service class: MessageAppService"
```

**Success Metrics:**
- [ ] No service class > 500 lines
- [ ] Each service has single responsibility
- [ ] Backward compatibility maintained

---

### Priority 2: Implement Repository Pattern 🔥🔥🔥
**Why Critical:**
- Handlers tightly coupled to MongoDB
- BsonDocument usage leaks infrastructure concerns
- Hard to switch database if needed

**Effort:** 3-4 weeks

**Impact:** 🔥🔥🔥 (Highest)

**Implementation Plan:**

1. **Week 1: Create Repository Interfaces**
   ```csharp
   // MyTelegram.Domain/Repositories/IUserRepository.cs
   public interface IUserRepository
   {
       Task<IUserReadModel?> GetByIdAsync(long userId, CancellationToken ct);
       Task<IUserReadModel?> GetByUsernameAsync(string username, CancellationToken ct);
       Task<IReadOnlyList<IUserReadModel>> GetByIdsAsync(IEnumerable<long> userIds, CancellationToken ct);
   }
   ```

2. **Week 2: Implement MongoDB Repositories**
   ```csharp
   // MyTelegram.QueryHandlers.MongoDB/Repositories/UserRepository.cs
   public class UserRepository : IUserRepository
   {
       private readonly IMongoDatabase _database;
       
       public async Task<IUserReadModel?> GetByIdAsync(long userId, CancellationToken ct)
       {
           var collection = _database.GetCollection<UserReadModel>("eventflow-userreadmodel");
           return await collection.Find(u => u.UserId == userId).FirstOrDefaultAsync(ct);
       }
   }
   ```

3. **Week 3-4: Migrate Handlers**
   ```csharp
   // Before
   var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
   var doc = await collection.Find(filter).FirstOrDefaultAsync();
   
   // After
   var user = await _userRepository.GetByIdAsync(userId, ct);
   ```

**Success Metrics:**
- [ ] All read models have repositories
- [ ] No handlers use BsonDocument directly
- [ ] All repositories documented

---

### Priority 3: Fix Exception Handling 🔥🔥
**Why Critical:**
- 233 files with generic catch (Exception)
- Hides real errors
- Makes debugging difficult

**Effort:** 1-2 weeks

**Impact:** 🔥🔥 (High)

**Implementation Plan:**

1. **Create Custom Exceptions**
   ```csharp
   // MyTelegram.Domain/Exceptions/
   public class DomainException : Exception { }
   public class ValidationException : DomainException { }
   public class AggregateNotFoundException : DomainException { }
   ```

2. **Replace Generic Catches**
   ```csharp
   // Before
   try { ... }
   catch (Exception ex) { logger.LogError(ex, "Error"); }
   
   // After
   try { ... }
   catch (RpcErrorException ex) { throw; }
   catch (MongoException ex) { logger.LogError(ex, "Database error"); throw; }
   catch (ValidationException ex) { return ValidationError(ex); }
   ```

**Success Metrics:**
- [ ] Zero generic catch (Exception) blocks
- [ ] All exceptions properly logged
- [ ] Custom exceptions for domain errors

---

### Priority 4: Add MongoDB Indexes 🔥🔥
**Why Critical:**
- Slow queries without indexes
- N+1 query problems
- Poor performance at scale

**Effort:** 1 week

**Impact:** 🔥🔥 (High)

**Implementation Plan:**

```javascript
// User indexes
db["eventflow-userreadmodel"].createIndex({ "UserId": 1 }, { unique: true })
db["eventflow-userreadmodel"].createIndex({ "PhoneNumber": 1 })
db["eventflow-userreadmodel"].createIndex({ "Usernames.Username": 1 })

// Message indexes
db["eventflow-messagereadmodel"].createIndex({ "MessageId": 1 }, { unique: true })
db["eventflow-messagereadmodel"].createIndex({ "SenderUserId": 1, "Date": -1 })

// Channel indexes
db["eventflow-channelreadmodel"].createIndex({ "ChannelId": 1 }, { unique: true })
db["eventflow-channelreadmodel"].createIndex({ "UserName": 1 })

// Fragment collectibles
db.fragment_collectibles.createIndex({ "username": 1 })
db.fragment_collectibles.createIndex({ "phone": 1 })
```

**Success Metrics:**
- [ ] All collections have proper indexes
- [ ] No queries > 100ms
- [ ] Zero N+1 queries

---

### Priority 5: Add Monitoring & Observability 🔥
**Why Critical:**
- Can't see performance problems
- Hard to debug production issues
- No visibility into system health

**Effort:** 1-2 weeks

**Impact:** 🔥 (Medium)

**Implementation Plan:**

1. **Add Prometheus Metrics**
   ```csharp
   services.AddPrometheusMetrics();
   
   private static readonly Counter MessagesSent = Metrics
       .CreateCounter("testgram_messages_sent_total", "Total messages sent");
   ```

2. **Add Structured Logging**
   ```csharp
   Log.Logger = new LoggerConfiguration()
       .WriteTo.Console()
       .WriteTo.Seq("http://seq:5341")
       .CreateLogger();
   ```

3. **Add Health Checks**
   ```csharp
   services.AddHealthChecks()
       .AddMongoDb(mongoConnectionString)
       .AddRabbitMQ(rabbitMqConnectionString)
       .AddRedis(redisConnectionString);
   ```

**Success Metrics:**
- [ ] Prometheus metrics exposed
- [ ] Grafana dashboards created
- [ ] Structured logging to Seq
- [ ] Health checks for all dependencies

---

## 📅 Timeline (10 Weeks)

```
Week 1-2:   Priority 1 (Refactor MessageAppService)
Week 3-4:   Priority 2 (Repository Pattern - Interfaces & Implementation)
Week 5-6:   Priority 2 (Repository Pattern - Migration)
Week 7:     Priority 3 (Exception Handling)
Week 8:     Priority 4 (MongoDB Indexes)
Week 9-10:  Priority 5 (Monitoring)
```

---

## 🎯 Success Criteria

After 10 weeks, the project should have:

- [ ] **Zero God Classes** (all services < 500 lines)
- [ ] **Repository pattern** implemented for all read models
- [ ] **Zero generic exception catching**
- [ ] **All MongoDB collections indexed**
- [ ] **Monitoring & observability** in place

---

## 🚀 Quick Wins (Do This Week)

1. **Add MongoDB indexes for users**
   ```javascript
   db["eventflow-userreadmodel"].createIndex({ "UserId": 1 })
   db["eventflow-userreadmodel"].createIndex({ "Usernames.Username": 1 })
   ```

2. **Start using CLAUDE_V2.md**
   - Use as primary development guide
   - Share with team

3. **Use Claude to refactor one service**
   ```
   "Refactor the service class: MessageAppService"
   ```

---

**Next Review:** 2026-05-03 (1 month)

**Owner:** Development Team

**Status:** 🟡 Planning → 🟢 In Progress
