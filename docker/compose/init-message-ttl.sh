#!/bin/bash
set -e

echo "Waiting for MongoDB to be ready..."
until mongosh --host mongodb:27017 --eval "db.adminCommand('ping')" > /dev/null 2>&1; do
  sleep 2
done

echo "Creating TTL index for message auto-delete..."
mongosh --host mongodb:27017 tg <<EOF
db['eventflow-messagereadmodel'].createIndex(
  { "ExpirationTime": 1 },
  {
    expireAfterSeconds: 0,
    name: "idx_message_expiration_ttl",
    partialFilterExpression: {
      "ExpirationTime": { \$exists: true, \$ne: null },
      "Deleted": { \$ne: true }
    }
  }
);
print("TTL index created successfully");
EOF

echo "Message TTL index setup completed"
