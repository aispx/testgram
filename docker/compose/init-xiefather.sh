#!/bin/bash
set -e

echo "Waiting for MongoDB to be ready..."
until mongosh --host mongodb:27017 --eval "db.adminCommand('ping')" > /dev/null 2>&1; do
  sleep 2
done

echo "MongoDB is ready. Checking XieFather bot..."

mongosh --host mongodb:27017 tg <<'EOF'
// Check if XieFather bot exists
var xieFatherUserId = NumberLong("600000000000");
var xieFatherUsername = "xiefather";

var existingBot = db.getCollection("eventflow-userreadmodel").findOne({ UserId: xieFatherUserId });

if (existingBot) {
    print("XieFather bot already exists with UserId: " + xieFatherUserId);
} else {
    print("Creating XieFather bot...");

    var now = new Date();

    db.getCollection("eventflow-userreadmodel").insertOne({
        "_id": "user-bot-xiefather",
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
        "FirstName": "XieFather",
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
        "UserId": xieFatherUserId,
        "UserName": xieFatherUsername,
        "Usernames": null,
        "UserNameUpdateDate": null,
        "IsDeleted": false,
        "Verified": true,
        "Version": NumberLong(1),
        "VideoEmojiMarkup": null,
        "DcId": 1
    });

    print("XieFather bot created successfully with UserId: " + xieFatherUserId);

    // Add username to eventflow-usernamereadmodel
    db.getCollection("eventflow-usernamereadmodel").insertOne({
        "_id": "username-xiefather",
        "UserName": xieFatherUsername,
        "PeerId": xieFatherUserId,
        "PeerType": 0,
        "IsActive": true
    });

    print("XieFather username registered in usernamereadmodel");
}

// Create index for xiefather-bot-state collection
db.getCollection("xiefather-bot-state").createIndex(
    { "OwnerId": 1 },
    { name: "idx_ownerid" }
);

db.getCollection("xiefather-bot-state").createIndex(
    { "Username": 1 },
    { unique: true, name: "idx_username" }
);

db.getCollection("xiefather-bot-state").createIndex(
    { "BotUserId": 1 },
    { unique: true, name: "idx_botuserid" }
);

print("XieFather bot initialization complete!");
EOF

echo "XieFather bot setup complete."
