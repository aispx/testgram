#!/bin/bash
# Auto-setup script for voice/video calls
# This script runs automatically when starting the server

set -e

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

DATABASE=${App__DatabaseName:-"tg"}

echo "========================================="
echo "Testgram Call System Auto-Setup"
echo "========================================="
echo ""

# Wait for MongoDB to be ready
echo "Waiting for MongoDB to be ready..."
for i in {1..30}; do
    if compose exec -T mongodb mongosh admin --quiet --eval "db.adminCommand('ping')" >/dev/null 2>&1; then
        echo "✓ MongoDB is ready"
        break
    fi
    if [ $i -eq 30 ]; then
        echo "✗ MongoDB is not responding after 30 seconds"
        exit 1
    fi
    sleep 1
done

echo ""
echo "Setting up call_sessions indexes..."

compose exec -T mongodb mongosh "$DATABASE" --quiet <<'EOF'
try {
    // Check if indexes already exist
    var indexes = db.call_sessions.getIndexes();
    var hasCallIdIndex = indexes.some(idx => idx.name === "idx_callid_accesshash");

    if (hasCallIdIndex) {
        print("✓ Indexes already exist, skipping...");
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

        print("✓ All indexes created successfully");
    }
} catch (e) {
    print("✗ Error creating indexes: " + e);
    quit(1);
}
EOF

if [ $? -eq 0 ]; then
    echo "✓ Call system setup completed successfully"
    echo ""
    echo "========================================="
    echo "Call System Status: READY"
    echo "========================================="
else
    echo "✗ Call system setup failed"
    exit 1
fi
