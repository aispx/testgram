#!/bin/bash

# Setup search indexes for better search performance

echo "Setting up search indexes in MongoDB..."

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

compose exec -T mongodb mongosh tg <<'EOF'

// Create text indexes for user search
db.getCollection("eventflow-userreadmodel").createIndex(
    {
        "UserName": "text",
        "FirstName": "text",
        "LastName": "text",
        "Phone": "text"
    },
    {
        name: "idx_user_search_text",
        weights: {
            "UserName": 10,
            "FirstName": 5,
            "LastName": 3,
            "Phone": 1
        },
        default_language: "english"
    }
);

// Create case-insensitive indexes for username search
db.getCollection("eventflow-userreadmodel").createIndex(
    { "UserName": 1 },
    {
        name: "idx_username_case_insensitive",
        collation: { locale: "en", strength: 2 }
    }
);

// Create index for first name search
db.getCollection("eventflow-userreadmodel").createIndex(
    { "FirstName": 1 },
    {
        name: "idx_firstname_case_insensitive",
        collation: { locale: "en", strength: 2 }
    }
);

// Create index for last name search
db.getCollection("eventflow-userreadmodel").createIndex(
    { "LastName": 1 },
    {
        name: "idx_lastname_case_insensitive",
        collation: { locale: "en", strength: 2 }
    }
);

// Create index for phone search
db.getCollection("eventflow-userreadmodel").createIndex(
    { "Phone": 1 },
    { name: "idx_phone" }
);

// Create text index for contact search
db.getCollection("eventflow-contactreadmodel").createIndex(
    {
        "FirstName": "text",
        "LastName": "text",
        "Phone": "text"
    },
    {
        name: "idx_contact_search_text",
        weights: {
            "FirstName": 5,
            "LastName": 3,
            "Phone": 1
        },
        default_language: "english"
    }
);

// Create compound index for contact search by user
db.getCollection("eventflow-contactreadmodel").createIndex(
    { "SelfUserId": 1, "FirstName": 1 },
    { name: "idx_contact_selfuser_firstname" }
);

// Create index for username search
db.getCollection("eventflow-usernamereadmodel").createIndex(
    { "UserName": 1 },
    {
        name: "idx_username_search",
        collation: { locale: "en", strength: 2 }
    }
);

// Create text index for message search
db.getCollection("eventflow-messagereadmodel").createIndex(
    {
        "Message": "text"
    },
    {
        name: "idx_message_search_text",
        default_language: "english"
    }
);

// Create compound index for message search by owner
db.getCollection("eventflow-messagereadmodel").createIndex(
    { "OwnerPeerId": 1, "MessageId": -1 },
    { name: "idx_message_owner_id" }
);

print("Search indexes created successfully!");

EOF

echo "Done!"
