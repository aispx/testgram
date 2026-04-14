// MongoDB initialization script for Forum Topics
// Creates forum_topics collection with indexes

db = db.getSiblingDB('tg');

// Create forum_topics collection if it doesn't exist
db.createCollection('forum_topics');

// Create indexes
db.forum_topics.createIndex({ "ChannelId": 1, "TopicId": 1 }, { unique: true, name: "idx_channel_topic" });
db.forum_topics.createIndex({ "ChannelId": 1, "Pinned": -1, "Date": -1 }, { name: "idx_channel_pinned_date" });
db.forum_topics.createIndex({ "ChannelId": 1, "Title": "text" }, { name: "idx_channel_title_text" });

print("Forum topics collection and indexes created successfully");
