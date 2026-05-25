#!/bin/bash
# Init container script to setup call indexes
set -e

echo "========================================="
echo "Testgram Call System Setup"
echo "========================================="

# Wait for MongoDB
echo "Waiting for MongoDB..."
until mongosh "$ConnectionStrings__Default/admin" --quiet --eval "db.adminCommand('ping')" >/dev/null 2>&1; do
    sleep 1
done
echo "✓ MongoDB is ready"

# Setup indexes
echo "Setting up call_sessions indexes..."
mongosh "$ConnectionStrings__Default/$App__DatabaseName" --quiet <<'EOF'
try {
    db.getCollectionNames().forEach(function(name) { if (name == "call_sessions") found = true; });
    if (typeof found === 'undefined') {
        db.createCollection("call_sessions");
        print("✓ call_sessions collection created");
    }
    var indexes = db.call_sessions.getIndexes();
    var hasCallIdIndex = indexes.some(idx => idx.name === "idx_callid_accesshash");

    if (hasCallIdIndex) {
        print("✓ Indexes already exist");
    } else {
        print("Creating indexes...");

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

        print("✓ All indexes created");
    }
} catch (e) {
    print("✗ Error: " + e);
    quit(1);
}
EOF

echo "✓ Call system setup completed"
echo "========================================="
