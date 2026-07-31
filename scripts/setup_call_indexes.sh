#!/bin/bash
# Script to setup call indexes in MongoDB
# Usage: ./setup_call_indexes.sh

MONGO_URL=${MONGO_URL:-"mongodb://127.0.0.1:27017"}
DATABASE=${DATABASE:-"tg"}

echo "Setting up call indexes in MongoDB..."
echo "MongoDB URL: $MONGO_URL"
echo "Database: $DATABASE"

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

compose exec -T mongodb mongosh "$DATABASE" <<'EOF'
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

print("\nIndexes created successfully!");
print("\nCreated indexes:");
db.call_sessions.getIndexes().forEach(function(idx) {
  print("  - " + idx.name);
});
EOF

echo ""
echo "Done!"
