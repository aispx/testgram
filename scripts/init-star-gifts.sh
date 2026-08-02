#!/bin/bash

# Star Gifts Initialization Script
# Creates all necessary collections and sample data for testing

echo "🎁 Initializing Star Gifts data..."

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

# Connect to MongoDB
compose exec -T mongodb mongosh tg --quiet --eval '

// 1. Create star-gifts collection with sample gifts
print("Creating star-gifts collection...");
db["star-gifts"].deleteMany({});
db["star-gifts"].insertMany([
  {
    GiftId: NumberLong("1"),
    Stars: NumberLong("100"),
    ConvertStars: NumberLong("80"),
    UpgradeStars: NumberLong("500"),
    Limited: false,
    SoldOut: false,
    Birthday: false,
    RequirePremium: false,
    LimitedPerUser: false,
    AvailabilityTotal: null,
    AvailabilityRemains: null,
    Title: "Blue Star",
    DocumentId: NumberLong("1000001"),
    DocumentAccessHash: NumberLong("9876543210"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("50000"),
    DcId: 2,
    IsAuction: false,
    RandomId: NumberLong("1")
  },
  {
    GiftId: NumberLong("2"),
    Stars: NumberLong("250"),
    ConvertStars: NumberLong("200"),
    UpgradeStars: NumberLong("1000"),
    Limited: true,
    SoldOut: false,
    Birthday: false,
    RequirePremium: false,
    LimitedPerUser: false,
    AvailabilityTotal: 1000,
    AvailabilityRemains: 850,
    FirstSaleDate: 1712160000,
    Title: "Golden Heart",
    DocumentId: NumberLong("1000002"),
    DocumentAccessHash: NumberLong("9876543211"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("55000"),
    DcId: 2,
    IsAuction: false,
    RandomId: NumberLong("2")
  },
  {
    GiftId: NumberLong("3"),
    Stars: NumberLong("500"),
    ConvertStars: NumberLong("400"),
    UpgradeStars: NumberLong("2000"),
    Limited: true,
    SoldOut: false,
    Birthday: true,
    RequirePremium: false,
    LimitedPerUser: false,
    AvailabilityTotal: 500,
    AvailabilityRemains: 450,
    FirstSaleDate: 1712160000,
    Title: "Birthday Cake",
    DocumentId: NumberLong("1000003"),
    DocumentAccessHash: NumberLong("9876543212"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("60000"),
    DcId: 2,
    IsAuction: false,
    RandomId: NumberLong("3")
  },
  {
    GiftId: NumberLong("4"),
    Stars: NumberLong("1000"),
    ConvertStars: NumberLong("800"),
    UpgradeStars: NumberLong("5000"),
    Limited: true,
    SoldOut: false,
    Birthday: false,
    RequirePremium: true,
    LimitedPerUser: true,
    AvailabilityTotal: 100,
    AvailabilityRemains: 95,
    PerUserTotal: 1,
    PerUserRemains: 1,
    FirstSaleDate: 1712160000,
    Title: "Diamond Crown",
    DocumentId: NumberLong("1000004"),
    DocumentAccessHash: NumberLong("9876543213"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("70000"),
    DcId: 2,
    IsAuction: false,
    RandomId: NumberLong("4")
  }
]);

// 2. Create star-gift-upgrade-config collection
print("Creating star-gift-upgrade-config...");
db["star-gift-upgrade-config"].deleteMany({});
db["star-gift-upgrade-config"].insertMany([
  // Model variants
  {
    Type: "model",
    Name: "Classic",
    RarityPermille: 500,
    DocumentId: NumberLong("2000001"),
    DocumentAccessHash: NumberLong("8765432100"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("45000"),
    DcId: 2
  },
  {
    Type: "model",
    Name: "Deluxe",
    RarityPermille: 300,
    DocumentId: NumberLong("2000002"),
    DocumentAccessHash: NumberLong("8765432101"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("50000"),
    DcId: 2
  },
  {
    Type: "model",
    Name: "Premium",
    RarityPermille: 150,
    DocumentId: NumberLong("2000003"),
    DocumentAccessHash: NumberLong("8765432102"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("55000"),
    DcId: 2
  },
  {
    Type: "model",
    Name: "Legendary",
    RarityPermille: 50,
    DocumentId: NumberLong("2000004"),
    DocumentAccessHash: NumberLong("8765432103"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("60000"),
    DcId: 2
  },
  // Pattern variants
  {
    Type: "pattern",
    Name: "Stars",
    RarityPermille: 400,
    DocumentId: NumberLong("2000005"),
    DocumentAccessHash: NumberLong("8765432104"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("40000"),
    DcId: 2
  },
  {
    Type: "pattern",
    Name: "Hearts",
    RarityPermille: 300,
    DocumentId: NumberLong("2000006"),
    DocumentAccessHash: NumberLong("8765432105"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("42000"),
    DcId: 2
  },
  {
    Type: "pattern",
    Name: "Diamonds",
    RarityPermille: 200,
    DocumentId: NumberLong("2000007"),
    DocumentAccessHash: NumberLong("8765432106"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("44000"),
    DcId: 2
  },
  {
    Type: "pattern",
    Name: "Galaxy",
    RarityPermille: 100,
    DocumentId: NumberLong("2000008"),
    DocumentAccessHash: NumberLong("8765432107"),
    FileReference: [],
    DocumentDate: 1712160000,
    MimeType: "application/x-tgsticker",
    DocumentSize: NumberLong("46000"),
    DcId: 2
  },
  // Backdrop variants
  {
    Type: "backdrop",
    Name: "Blue Sky",
    RarityPermille: 400,
    BackdropId: 1,
    CenterColor: 0x87CEEB,
    EdgeColor: 0x4682B4,
    PatternColor: 0xFFFFFF,
    TextColor: 0x000000
  },
  {
    Type: "backdrop",
    Name: "Sunset",
    RarityPermille: 300,
    BackdropId: 2,
    CenterColor: 0xFF6347,
    EdgeColor: 0xFF4500,
    PatternColor: 0xFFD700,
    TextColor: 0xFFFFFF
  },
  {
    Type: "backdrop",
    Name: "Ocean",
    RarityPermille: 200,
    BackdropId: 3,
    CenterColor: 0x006994,
    EdgeColor: 0x003D5C,
    PatternColor: 0x00CED1,
    TextColor: 0xFFFFFF
  },
  {
    Type: "backdrop",
    Name: "Aurora",
    RarityPermille: 100,
    BackdropId: 4,
    CenterColor: 0x9370DB,
    EdgeColor: 0x4B0082,
    PatternColor: 0x00FF7F,
    TextColor: 0xFFFFFF
  }
]);

// 3. Create indexes
print("Creating indexes...");
db["star-gifts"].createIndex({ "GiftId": 1 }, { unique: true });
db["unique-star-gifts"].createIndex({ "Slug": 1 }, { unique: true });
db["unique-star-gifts"].createIndex({ "OwnerUserId": 1 });
db["unique-star-gifts"].createIndex({ "GiftId": 1 });
db["unique-star-gifts"].createIndex({ "ResellStars": 1 });
// Indexes for saved-star-gifts match the current SavedStarGiftDocument model
// (OwnerUserId/OwnerChannelId + MessageId/RandomId/UniqueSlug).
db["saved-star-gifts"].createIndex({ "OwnerUserId": 1, "MessageId": 1 });
db["saved-star-gifts"].createIndex({ "OwnerChannelId": 1, "RandomId": 1 });
db["saved-star-gifts"].createIndex({ "OwnerUserId": 1, "UniqueSlug": 1 });
db["saved-star-gifts"].createIndex({ "OwnerUserId": 1 });
db["star-gift-offers"].createIndex({ "RecipientUserId": 1, "Resolved": 1 });
db["star-gift-offers"].createIndex({ "Slug": 1 });
db["star-gift-collections"].createIndex({ "UserId": 1 });

print("✅ Star Gifts initialization complete!");
print("Created " + db["star-gifts"].countDocuments({}) + " star gifts");
print("Created " + db["star-gift-upgrade-config"].countDocuments({}) + " upgrade variants");

'

echo "✅ Star Gifts data initialized successfully!"
echo ""
echo "Available gifts:"
echo "  1. Blue Star (100 stars)"
echo "  2. Golden Heart (250 stars, limited)"
echo "  3. Birthday Cake (500 stars, limited, birthday)"
echo "  4. Diamond Crown (1000 stars, limited, premium)"
echo ""
echo "Upgrade variants: 16 total (4 models, 4 patterns, 4 backdrops)"
