#!/bin/bash
set -e

echo "Waiting for MongoDB to be ready..."
until mongosh --host mongodb:27017 --eval "db.adminCommand('ping')" > /dev/null 2>&1; do
  sleep 2
done

echo "MongoDB is ready. Creating business bot indexes..."

mongosh --host mongodb:27017 tg <<EOF
// Create indexes for connected_business_bots collection
db.connected_business_bots.createIndex(
    { "UserId": 1 },
    { name: "idx_userid" }
);

db.connected_business_bots.createIndex(
    { "BotId": 1 },
    { name: "idx_botid" }
);

db.connected_business_bots.createIndex(
    { "ConnectionId": 1 },
    { unique: true, name: "idx_connectionid" }
);

db.connected_business_bots.createIndex(
    { "UserId": 1, "BotId": 1 },
    { unique: true, name: "idx_userid_botid" }
);

// Create indexes for paused_business_bot_chats collection
db.paused_business_bot_chats.createIndex(
    { "UserId": 1, "PeerId": 1 },
    { unique: true, name: "idx_userid_peerid" }
);

db.paused_business_bot_chats.createIndex(
    { "UserId": 1 },
    { name: "idx_userid" }
);

// Create index for counters collection (for Qts)
db.counters.createIndex(
    { "_id": 1 },
    { name: "idx_id" }
);

print("Business bot indexes created successfully!");
EOF

echo "Business bot indexes setup complete."
