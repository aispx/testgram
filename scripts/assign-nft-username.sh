#!/bin/bash

# Assign NFT Username to User
# Usage: ./assign-nft-username.sh <user_id> <nft_username>

if [ "$#" -ne 2 ]; then
    echo "Usage: $0 <user_id> <nft_username>"
    echo "Example: $0 2010001 blockchain"
    exit 1
fi

USER_ID=$1
NFT_USERNAME=$2

echo "🔷 Assigning NFT username '$NFT_USERNAME' to user $USER_ID..."

cd /root/testgram/docker/compose

# Check if Fragment collectible exists
COLLECTIBLE_EXISTS=$(docker-compose exec -T mongodb mongosh tg --quiet --eval "
db.fragment_collectibles.countDocuments({username: '$NFT_USERNAME'})
")

if [ "$COLLECTIBLE_EXISTS" = "0" ]; then
    echo "❌ Error: Fragment collectible '$NFT_USERNAME' not found!"
    echo "Available NFT usernames:"
    docker-compose exec -T mongodb mongosh tg --quiet --eval "
    db.fragment_collectibles.find({type: 'username'}).forEach(doc => print('  - ' + doc.username))
    "
    exit 1
fi

# Get user's current usernames
echo "Fetching user's current usernames..."
CURRENT_USERNAME=$(docker-compose exec -T mongodb mongosh tg --quiet --eval "
var user = db['eventflow-userreadmodel'].findOne({UserId: NumberLong('$USER_ID')});
if (user) print(user.UserName || '');
")

if [ -z "$CURRENT_USERNAME" ]; then
    echo "❌ Error: User $USER_ID not found!"
    exit 1
fi

echo "Current username: $CURRENT_USERNAME"

# Update user's Usernames array
echo "Adding NFT username to user..."
docker-compose exec -T mongodb mongosh tg --quiet --eval "
db['eventflow-userreadmodel'].updateOne(
  { UserId: NumberLong('$USER_ID') },
  {
    \$set: {
      Usernames: [
        { Username: '$CURRENT_USERNAME', Editable: true, Active: true },
        { Username: '$NFT_USERNAME', Editable: false, Active: true }
      ]
    }
  }
)
"

echo "✅ NFT username '$NFT_USERNAME' assigned to user $USER_ID!"
echo ""
echo "User now has:"
echo "  - $CURRENT_USERNAME (basic, editable)"
echo "  - $NFT_USERNAME (NFT, Fragment collectible)"
echo ""
echo "⚠️  Important: User must restart Telegram client and clear cache to see changes!"
echo ""
echo "To view Fragment info, click on the username in profile."
