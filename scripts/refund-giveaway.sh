#!/bin/bash

# Usage: ./refund-giveaway.sh <channel_id> <giveaway_id>
# Example: ./refund-giveaway.sh 800000000001 giveaway-800000000001--2835751601633377161

if [ -z "$1" ] || [ -z "$2" ]; then
    echo "Usage: $0 <channel_id> <giveaway_id>"
    echo "Example: $0 800000000001 giveaway-800000000001--2835751601633377161"
    exit 1
fi

CHANNEL_ID=$1
GIVEAWAY_ID=$2

cd "$(dirname "$0")/../docker/compose"

echo "Refunding giveaway $GIVEAWAY_ID in channel $CHANNEL_ID..."

docker-compose exec -T mongodb mongosh tg --quiet --eval "
db.giveaways.updateOne(
  { _id: '$GIVEAWAY_ID' },
  {
    \$set: {
      Refunded: true,
      Status: 'finished',
      RefundedAt: new Date()
    }
  }
)
"

echo "Done! Giveaway marked as refunded."
echo "Users will see: 'The channel canceled the prizes by refunding the payment for them.'"
