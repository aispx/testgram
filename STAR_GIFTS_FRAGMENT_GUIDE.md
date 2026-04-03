# Star Gifts & Fragment NFT - Complete Setup Guide

## 📊 Current Status

### ✅ Star Gifts (Fully Configured)
- **4 Star Gifts** available for purchase
- **16 Upgrade Variants** (4 models, 4 patterns, 4 backdrops)
- All handlers implemented and working
- MongoDB collections initialized

### ✅ Fragment NFT Usernames (Fully Configured)
- **5 NFT Usernames** available
- **3 NFT Phone Numbers** available
- Fragment collectible info handler working
- Username toggle/reorder handlers working

---

## 🎁 Available Star Gifts

| ID | Name | Stars | Convert | Upgrade | Type |
|----|------|-------|---------|---------|------|
| 1 | Blue Star | 100 | 80 | 500 | Regular |
| 2 | Golden Heart | 250 | 200 | 1000 | Limited (850/1000) |
| 3 | Birthday Cake | 500 | 400 | 2000 | Limited, Birthday |
| 4 | Diamond Crown | 1000 | 800 | 5000 | Limited, Premium |

### Upgrade Variants (16 total)

**Models (4):**
- Classic (50% rarity)
- Deluxe (30% rarity)
- Premium (15% rarity)
- Legendary (5% rarity)

**Patterns (4):**
- Stars (40% rarity)
- Hearts (30% rarity)
- Diamonds (20% rarity)
- Galaxy (10% rarity)

**Backdrops (4):**
- Blue Sky (40% rarity)
- Sunset (30% rarity)
- Ocean (20% rarity)
- Aurora (10% rarity)

---

## 🔷 Available Fragment NFT Usernames

| Username | Price (USD) | Price (TON) | Status |
|----------|-------------|-------------|--------|
| crypto | $50,000 | 150 TON | Available |
| blockchain | $14,500 | 50 TON | Available |
| defi | $25,000 | 80 TON | Available |
| nft | $30,000 | 100 TON | Available |
| web3 | $20,000 | 70 TON | Available |

### Available Fragment Phone Numbers

| Phone | Price (USD) | Price (TON) |
|-------|-------------|-------------|
| 888123456 | $29,900 | 100 TON |
| 888777888 | $45,000 | 150 TON |
| 888999999 | $60,000 | 200 TON |

---

## 🚀 How to Use

### Assign NFT Username to User

```bash
cd /root/testgram
./scripts/assign-nft-username.sh <user_id> <nft_username>

# Example:
./scripts/assign-nft-username.sh 2010001 blockchain
```

This will:
1. Check if Fragment collectible exists
2. Add NFT username to user's Usernames array
3. Set Editable=false (NFT username)
4. Keep basic username as Editable=true

**Important:** User must restart Telegram client and clear cache to see changes!

### View Fragment Info in Client

1. Open user profile
2. Click on NFT username (shows Fragment icon)
3. See purchase info:
   - Purchase date
   - Price in USD and TON
   - Link to Fragment.com

---

## 🧪 Testing Star Gifts

### 1. Get Available Gifts

**Method:** `payments.getStarGifts`

**Request:**
```json
{
  "hash": 0
}
```

**Response:** Returns all 4 star gifts with details

### 2. Send Star Gift

**Method:** `payments.sendStarGift` (via payment form)

**Steps:**
1. Get payment form with `inputInvoiceStarGift`
2. Specify recipient peer
3. Set gift_id (1-4)
4. Optional: hide_name, include_upgrade, message

### 3. Upgrade Star Gift

**Method:** `payments.upgradeStarGift`

**Steps:**
1. Get upgrade preview: `payments.getStarGiftUpgradePreview`
2. See random attributes (model, pattern, backdrop)
3. Pay upgrade_stars to confirm
4. Gift becomes unique collectible

### 4. Manage Collectibles

**Available operations:**
- `payments.getSavedStarGifts` - List received gifts
- `payments.saveStarGift` - Pin/unpin to profile
- `payments.convertStarGift` - Convert to Stars
- `payments.transferStarGift` - Transfer to another user
- `payments.updateStarGiftPrice` - List for resale
- `payments.getStarGiftWithdrawalUrl` - Export to TON NFT

---

## 🧪 Testing Fragment NFT

### 1. Assign NFT Username

```bash
./scripts/assign-nft-username.sh 2010001 crypto
```

### 2. View in Client

1. Open profile of user 2010001
2. See two usernames:
   - Basic username (editable)
   - crypto (NFT, Fragment icon)

### 3. Click NFT Username

Opens `FragmentUsernameBottomSheet` showing:
- Purchase date
- Price: $50,000 USD
- Price: 150 TON
- Link to Fragment.com

### 4. Toggle Username

**Method:** `account.toggleUsername`

```json
{
  "username": "crypto",
  "active": false
}
```

Deactivates NFT username (can reactivate later)

### 5. Reorder Usernames

**Method:** `account.reorderUsernames`

```json
{
  "order": ["crypto", "basicusername"]
}
```

Changes primary username order

---

## 📁 MongoDB Collections

### Star Gifts Collections

```javascript
// Available gifts
db["star-gifts"].find()

// Upgrade variants
db["star-gift-upgrade-config"].find()

// User's saved gifts
db["saved-star-gifts"].find({ UserId: NumberLong("2010001") })

// Unique collectibles
db["unique-star-gifts"].find({ OwnerUserId: NumberLong("2010001") })

// Gift offers
db["star-gift-offers"].find({ RecipientUserId: NumberLong("2010001") })

// Gift collections
db["star-gift-collections"].find({ UserId: NumberLong("2010001") })
```

### Fragment Collections

```javascript
// Fragment collectibles
db.fragment_collectibles.find()

// User with NFT usernames
db["eventflow-userreadmodel"].findOne({ UserId: NumberLong("2010001") })
```

---

## 🔧 Maintenance Scripts

### Re-initialize Star Gifts

```bash
cd /root/testgram
./scripts/init-star-gifts.sh
```

### Re-initialize Fragment NFT

```bash
cd /root/testgram
./scripts/init-fragment-nft.sh
```

### Add More Star Gifts

```javascript
db["star-gifts"].insertOne({
  GiftId: NumberLong("5"),
  Stars: NumberLong("2000"),
  ConvertStars: NumberLong("1600"),
  UpgradeStars: NumberLong("10000"),
  Limited: true,
  SoldOut: false,
  Birthday: false,
  RequirePremium: true,
  LimitedPerUser: true,
  AvailabilityTotal: 50,
  AvailabilityRemains: 50,
  PerUserTotal: 1,
  PerUserRemains: 1,
  Title: "Platinum Star",
  DocumentId: NumberLong("1000005"),
  DocumentAccessHash: NumberLong("9876543214"),
  FileReference: [],
  DocumentDate: 1712160000,
  MimeType: "application/x-tgsticker",
  DocumentSize: NumberLong("80000"),
  DcId: 2,
  IsAuction: false,
  RandomId: NumberLong("5")
})
```

### Add More Fragment Collectibles

```javascript
db.fragment_collectibles.insertOne({
  _id: "fragment-username-bitcoin",
  type: "username",
  username: "bitcoin",
  purchase_date: Math.floor(Date.now() / 1000),
  currency: "USD",
  amount: 100000,
  crypto_currency: "TON",
  crypto_amount: NumberLong("300000000000"),
  url: "https://fragment.com/username/bitcoin"
})
```

---

## 🐛 Troubleshooting

### Star Gifts Not Showing

1. Check MongoDB:
   ```bash
   cd /root/testgram/docker/compose
   docker-compose exec -T mongodb mongosh tg --quiet --eval 'db["star-gifts"].countDocuments({})'
   ```

2. Re-initialize if needed:
   ```bash
   cd /root/testgram
   ./scripts/init-star-gifts.sh
   ```

3. Restart servers:
   ```bash
   cd /root/testgram/docker/compose
   docker-compose restart messenger-command-server messenger-query-server
   ```

### NFT Username Not Showing

1. Check Fragment collectible exists:
   ```bash
   cd /root/testgram/docker/compose
   docker-compose exec -T mongodb mongosh tg --quiet --eval 'db.fragment_collectibles.find({username: "blockchain"})'
   ```

2. Check user's Usernames field:
   ```bash
   docker-compose exec -T mongodb mongosh tg --quiet --eval 'db["eventflow-userreadmodel"].findOne({UserId: NumberLong("2010001")}, {Usernames: 1})'
   ```

3. User must:
   - Kill Telegram app
   - Clear cache
   - Restart app
   - Re-login if needed

### Fragment Info Not Loading

1. Check GetCollectibleInfoHandler logs:
   ```bash
   docker-compose logs -f messenger-query-server | grep Collectible
   ```

2. Verify handler is working:
   - Click NFT username in profile
   - Should open FragmentUsernameBottomSheet
   - Shows purchase info from MongoDB

---

## 📚 API Documentation

### Star Gifts API
- https://core.telegram.org/api/gifts

### Fragment API
- https://fragment.com

### TL Schema
- Use `/schema.jppgr.am search starGift` for constructors
- Use `/schema.jppgr.am search fragment` for Fragment types

---

## ✅ Verification Checklist

- [ ] Star gifts show in client (4 gifts)
- [ ] Can send star gift to user
- [ ] Can upgrade star gift (16 variants)
- [ ] Can view saved gifts
- [ ] Can convert gift to stars
- [ ] NFT username shows in profile
- [ ] NFT username has Fragment icon
- [ ] Clicking NFT username shows purchase info
- [ ] Can toggle NFT username active/inactive
- [ ] Can reorder usernames

---

**Last Updated:** 2026-04-03

**Status:** ✅ Fully Configured and Ready for Testing
