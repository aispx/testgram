// MongoDB script to create indexes for call_sessions collection
// Run with: mongosh tg < setup_call_indexes.js

print("Creating indexes for call_sessions collection...");

db.call_sessions.createIndex(
  { CallId: 1, AccessHash: 1 },
  { name: "idx_callid_accesshash", unique: true }
);

db.call_sessions.createIndex(
  { CallerId: 1, Date: -1 },
  { name: "idx_callerid_date" }
);

db.call_sessions.createIndex(
  { CalleeId: 1, Date: -1 },
  { name: "idx_calleeid_date" }
);

db.call_sessions.createIndex(
  { State: 1, Date: -1 },
  { name: "idx_state_date" }
);

db.call_sessions.createIndex(
  { Date: -1 },
  { name: "idx_date", expireAfterSeconds: 2592000 }
);

print("Indexes created successfully!");
print("\nCreated indexes:");
db.call_sessions.getIndexes().forEach(function(idx) {
  print("  - " + idx.name);
});
