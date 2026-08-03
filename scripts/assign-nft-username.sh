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

if [[ ! "$USER_ID" =~ ^[0-9]+$ ]]; then
    echo "❌ Error: user_id must be a positive integer" >&2
    exit 1
fi

if [ -z "$NFT_USERNAME" ]; then
    echo "❌ Error: nft_username must not be empty" >&2
    exit 1
fi

echo "🔷 Assigning NFT username '$NFT_USERNAME' to user $USER_ID..."

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

# Check if Fragment collectible exists
COLLECTIBLE_EXISTS=$(compose exec -T mongodb mongosh tg --quiet --eval "
db.fragment_collectibles.countDocuments({username: '$NFT_USERNAME'})
")

if [ "$COLLECTIBLE_EXISTS" = "0" ]; then
    echo "❌ Error: Fragment collectible '$NFT_USERNAME' not found!"
    echo "Available NFT usernames:"
    compose exec -T mongodb mongosh tg --quiet --eval "
    db.fragment_collectibles.find({type: 'username'}).forEach(doc => print('  - ' + doc.username))
    "
    exit 1
fi

# Validate the user exists and resolve the primary (editable) username.
# NOTE: a user can exist with an empty legacy UserName field while having a
# populated Usernames array, so we must check the document itself, not UserName.
echo "Fetching user's current usernames..."
CURRENT_USERNAME=$(compose exec -T mongodb mongosh tg --quiet --eval "
var user = db['eventflow-userreadmodel'].findOne({UserId: NumberLong('$USER_ID')});
if (!user) {
    print('ERROR:USER_NOT_FOUND');
    quit(2);
}

var primary = user.UserName || '';
if (!primary && user.Usernames && user.Usernames.length > 0) {
    var editable = user.Usernames.find(function(u) { return u.Editable && u.Active; });
    var anyActive = user.Usernames.find(function(u) { return u.Active; });
    primary = (editable || anyActive || user.Usernames[0]).Username;
}
print(primary || '');
" | tr -d '\r')
MONGO_STATUS=$?

if grep -q '^ERROR:USER_NOT_FOUND$' <<<"$CURRENT_USERNAME"; then
    echo "❌ Error: User $USER_ID not found!"
    exit 1
fi

if [ "$MONGO_STATUS" -ne 0 ]; then
    echo "❌ Error: mongosh failed with status $MONGO_STATUS" >&2
    if [ -n "$CURRENT_USERNAME" ]; then
        echo "$CURRENT_USERNAME" >&2
    fi
    exit "$MONGO_STATUS"
fi

if [ -z "$CURRENT_USERNAME" ]; then
    echo "❌ Error: User $USER_ID has no username to base the NFT username on!" >&2
    exit 1
fi

echo "Current username: $CURRENT_USERNAME"

# Update user's Usernames array, merging with existing usernames so we don't
# drop already-assigned usernames (e.g. other NFT usernames).
echo "Adding NFT username to user..."
compose exec -T mongodb mongosh tg --quiet --eval "
var user = db['eventflow-userreadmodel'].findOne({UserId: NumberLong('$USER_ID')});
if (!user) {
    print('ERROR:USER_NOT_FOUND');
    quit(2);
}

var usernames = [];
if (user.Usernames && user.Usernames.length > 0) {
    usernames = user.Usernames.map(function(u) {
        return { Username: u.Username, Editable: u.Editable, Active: u.Active };
    });
} else {
    usernames.push({ Username: '$CURRENT_USERNAME', Editable: true, Active: true });
}

var alreadyAssigned = usernames.some(function(u) { return u.Username === '$NFT_USERNAME'; });
if (!alreadyAssigned) {
    usernames.push({ Username: '$NFT_USERNAME', Editable: false, Active: true });
}

db['eventflow-userreadmodel'].updateOne(
  { UserId: NumberLong('$USER_ID') },
  { \$set: { Usernames: usernames } }
);
print('OK');
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
