// Create TTL index for channel_admin_log collection
// Records will be automatically deleted 48 hours after creation

db = db.getSiblingDB('tg');

// Create TTL index on 'date' field with 48 hours (172800 seconds) expiration
db.channel_admin_log.createIndex(
    { "date": 1 },
    { 
        expireAfterSeconds: 172800,  // 48 hours = 48 * 60 * 60 = 172800 seconds
        name: "admin_log_ttl_48h"
    }
);

print("✓ Created TTL index on channel_admin_log.date (48 hours expiration)");
