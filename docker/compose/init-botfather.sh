#!/bin/bash
set -e

echo "Waiting for MongoDB to be ready..."
until mongosh --host mongodb:27017 --eval "db.adminCommand('ping')" > /dev/null 2>&1; do
  sleep 2
done

echo "MongoDB is ready. Checking BotFather bot..."

mongosh --host mongodb:27017 tg <<'EOF'
// Check if BotFather bot exists
var botFatherUserId = NumberLong("600000000000");
var botFatherUsername = "botfather";
var legacyUsername = "xie" + "father";
var now = new Date();

// Move existing state from the old collection name.
var oldStateCollection = legacyUsername + "-bot-state";
var newStateCollection = "botfather-bot-state";
var collectionNames = db.getCollectionNames();
if (collectionNames.indexOf(oldStateCollection) >= 0) {
    if (collectionNames.indexOf(newStateCollection) < 0) {
        db.getCollection(oldStateCollection).renameCollection(newStateCollection);
        print("Renamed legacy bot state collection to botfather");
    } else {
        db.getCollection(oldStateCollection).find().forEach(function(doc) {
            db.getCollection(newStateCollection).replaceOne({ "_id": doc._id }, doc, { upsert: true });
        });
        db.getCollection(oldStateCollection).drop();
        print("Merged legacy bot state collection into botfather");
    }
}

// Free @botfather if it was previously assigned to a channel.
var channelRelease = db.getCollection("eventflow-channelreadmodel").updateMany(
    { "UserName": botFatherUsername },
    { "$set": { "UserName": null } }
);
if (channelRelease.modifiedCount > 0) {
    print("Released @botfather from channels: " + channelRelease.modifiedCount);
}

var usernameCollection = db.getCollection("eventflow-usernamereadmodel");
usernameCollection.deleteMany({ "UserName": botFatherUsername, "PeerId": { "$ne": botFatherUserId } });
usernameCollection.deleteMany({ "_id": "username-botfather", "PeerId": { "$ne": botFatherUserId } });
usernameCollection.deleteMany({ "UserName": legacyUsername });
usernameCollection.deleteMany({ "_id": "username-" + legacyUsername });

var existingBot = db.getCollection("eventflow-userreadmodel").findOne({ UserId: botFatherUserId });

if (existingBot) {
    print("BotFather bot already exists with UserId: " + botFatherUserId);
    db.getCollection("eventflow-userreadmodel").updateOne(
        { "UserId": botFatherUserId },
        {
            "$set": {
                "FirstName": "BotFather",
                "UserName": botFatherUsername,
                "LastUpdateDate": now,
                "Bot": true,
                "Verified": true,
                "IsDeleted": false
            }
        }
    );
} else {
    print("Creating BotFather bot...");

    db.getCollection("eventflow-userreadmodel").insertOne({
        "_id": "user-bot-botfather",
        "About": "I can help you create and manage bots. Use /start to begin.",
        "AccessHash": NumberLong(Math.floor(Math.random() * 9007199254740991)),
        "AccountTtl": 365,
        "Birthday": null,
        "Bot": true,
        "BotActiveUsers": null,
        "BotHasMainApp": false,
        "BotInfoVersion": 1,
        "BotChatHistory": false,
        "BotNochats": false,
        "Color": {
            "Color": 0,
            "BackgroundEmojiId": null
        },
        "CreationTime": now,
        "Email": null,
        "EmojiStatusDocumentId": null,
        "EmojiStatusValidUntil": null,
        "FallbackPhotoId": null,
        "FirstName": "BotFather",
        "GlobalPrivacySettings": {
            "ArchiveAndMuteNewNoncontactPeers": false,
            "KeepArchivedUnmuted": false,
            "KeepArchivedFolders": false,
            "HideReadMarks": false,
            "NewNoncontactPeersRequirePremium": false,
            "NoncontactPeersPaidStars": null,
            "DisallowUnlimitedStargifts": false,
            "DisallowLimitedStargifts": false,
            "DisallowUniqueStargifts": false,
            "DisallowPremiumGifts": false
        },
        "HasPassword": false,
        "IsOnline": false,
        "LastName": "",
        "LastUpdateDate": now,
        "PersonalChannelId": null,
        "PersonalPhotoId": null,
        "PhoneNumber": "0",
        "PinnedMsgId": null,
        "PinnedMsgIdList": [],
        "Premium": false,
        "ProfileColor": null,
        "ProfilePhoto": null,
        "ProfilePhotoId": null,
        "ProfilePhotoUpdateDate": null,
        "RecentEmojiStatuses": [],
        "SensitiveCanChange": false,
        "SensitiveEnabled": false,
        "ShowContactSignUpNotification": false,
        "Fake": false,
        "Scam": false,
        "Support": false,
        "UserId": botFatherUserId,
        "UserName": botFatherUsername,
        "Usernames": null,
        "UserNameUpdateDate": null,
        "IsDeleted": false,
        "Verified": true,
        "Version": NumberLong(1),
        "VideoEmojiMarkup": null,
        "DcId": 1
    });

    print("BotFather bot created successfully with UserId: " + botFatherUserId);
}

// Add or repair username in eventflow-usernamereadmodel
usernameCollection.updateOne(
    { "_id": "username-botfather" },
    {
        "$set": {
            "UserName": botFatherUsername,
            "PeerId": botFatherUserId,
            "PeerType": 3,
            "IsActive": true
        }
    },
    { upsert: true }
);

print("BotFather username registered in usernamereadmodel");

// Create index for botfather-bot-state collection
db.getCollection("botfather-bot-state").createIndex(
    { "OwnerId": 1 },
    { name: "idx_ownerid" }
);

db.getCollection("botfather-bot-state").createIndex(
    { "Username": 1 },
    { unique: true, name: "idx_username" }
);

db.getCollection("botfather-bot-state").createIndex(
    { "BotUserId": 1 },
    { unique: true, name: "idx_botuserid" }
);

print("BotFather bot initialization complete!");
EOF

echo "BotFather bot setup complete."
