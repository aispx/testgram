#!/bin/bash

# Give Stars to User
# Usage: ./give-stars.sh <user_id> <stars_amount>

if [ "$#" -ne 2 ]; then
    echo "Usage: $0 <user_id> <stars_amount>"
    echo "Example: $0 2010001 1000"
    exit 1
fi

USER_ID=$1
STARS=$2

echo "⭐ Giving $STARS stars to user $USER_ID..."

cd /root/testgram/docker/compose

# Check if user exists
USER_EXISTS=$(docker-compose exec -T mongodb mongosh tg --quiet --eval "
db['eventflow-userreadmodel'].countDocuments({UserId: NumberLong('$USER_ID')})
")

if [ "$USER_EXISTS" = "0" ]; then
    echo "❌ Error: User $USER_ID not found!"
    exit 1
fi

# Get current balance
CURRENT_BALANCE=$(docker-compose exec -T mongodb mongosh tg --quiet --eval "
var user = db['eventflow-userreadmodel'].findOne({UserId: NumberLong('$USER_ID')});
if (user && user.StarsBalance) {
  if (typeof user.StarsBalance === 'object') print(user.StarsBalance.toString());
  else print(user.StarsBalance);
} else print('0');
" | tr -d '\r')

echo "Current balance: $CURRENT_BALANCE stars"

# Calculate new balance
NEW_BALANCE=$((CURRENT_BALANCE + STARS))

# Update user's stars balance
docker-compose exec -T mongodb mongosh tg --quiet --eval "
db['eventflow-userreadmodel'].updateOne(
  { UserId: NumberLong('$USER_ID') },
  { \$set: { StarsBalance: NumberLong('$NEW_BALANCE') } }
)
"

echo "✅ Successfully gave $STARS stars to user $USER_ID!"
echo "New balance: $NEW_BALANCE stars"
