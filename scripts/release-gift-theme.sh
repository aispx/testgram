#!/bin/bash

# Script to release a theme for ALL unique star gifts (NFTs) of a star gift type
# Usage: ./release-gift-theme.sh <gift_id> <center_color> [edge_color] [pattern_color] [text_color]
#
# gift_id is the star-gift id from the star-gifts collection (NOT the NFT id).
# The theme is applied to every unique-star-gifts doc that has this GiftId.

if [ "$#" -lt 2 ]; then
    echo "Usage: $0 <gift_id> <center_color> [edge_color] [pattern_color] [text_color]"
    echo "Example: $0 900 0x3390ec 0x6fb1f6 0x8ac5f8 0xffffff"
    exit 1
fi

GIFT_ID=$1
CENTER_COLOR=${2:-0x3390ec}
EDGE_COLOR=${3:-0x6fb1f6}
PATTERN_COLOR=${4:-0x8ac5f8}
TEXT_COLOR=${5:-0xffffff}

echo "🎨 Releasing theme for ALL unique gifts of gift #$GIFT_ID..."

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

compose exec -T mongodb mongosh tg --quiet --eval "
const giftId = NumberLong('$GIFT_ID');
const centerColor = $CENTER_COLOR;
const edgeColor = $EDGE_COLOR;
const patternColor = $PATTERN_COLOR;
const textColor = $TEXT_COLOR;

// Calculate darker colors for dark theme
function darkenColor(color) {
    const r = Math.floor(((color >> 16) & 0xFF) * 0.8);
    const g = Math.floor(((color >> 8) & 0xFF) * 0.8);
    const b = Math.floor((color & 0xFF) * 0.8);
    return (r << 16) | (g << 8) | b;
}

const darkCenterColor = darkenColor(centerColor);
const darkEdgeColor = darkenColor(edgeColor);

const themeSettings = [
    {
        BaseTheme: 'classic',
        AccentColor: centerColor,
        OutboxAccentColor: edgeColor,
        MessageColorsAnimated: true,
        MessageColors: [centerColor, edgeColor],
        Wallpaper: {
            Id: NumberLong(0),
            Dark: false,
            Settings: {
                BackgroundColor: 0xFFFFFFFF,
                Intensity: 0
            }
        }
    },
    {
        BaseTheme: 'night',
        AccentColor: darkCenterColor,
        MessageColorsAnimated: true,
        MessageColors: [darkCenterColor, darkEdgeColor],
        Wallpaper: {
            Id: NumberLong(0),
            Dark: true,
            Settings: {
                BackgroundColor: 0xFF0F0F0F,
                Intensity: 0
            }
        }
    }
];

// 1. Store the theme on the GIFT TYPE (star-gifts by GiftId). This is the
//    source of truth: every NFT of this gift, including ones upgraded or
//    transferred after the release, inherits the theme automatically.
const giftTypeResult = db['star-gifts'].updateOne(
    { GiftId: giftId },
    { \$set: { ThemeAvailable: true, ThemeSettings: themeSettings } }
);

if (giftTypeResult.matchedCount === 0) {
    print('❌ Error: Gift #' + giftId + ' not found in star-gifts');
    quit(1);
}

// 2. Also stamp the theme onto all EXISTING NFTs of this gift type so legacy
//    clients / already-sent messages pick it up immediately.
const updateResult = db['unique-star-gifts'].updateMany(
    { GiftId: giftId },
    { \$set: { ThemeAvailable: true, ThemeSettings: themeSettings } }
);

// Collect the NFT ids that got the theme so we can update saved gifts
const uniqueIds = db['unique-star-gifts']
    .find({ GiftId: giftId }, { UniqueId: 1 })
    .toArray()
    .map(g => g.UniqueId);

// 3. Update saved gifts to mark theme as available.
//    The unique gift id is stored in RandomId on saved-star-gifts
//    (RandomId = uniqueDoc.UniqueId, see UpgradeStarGiftHandler).
const savedResult = db['saved-star-gifts'].updateMany(
    { RandomId: { \$in: uniqueIds } },
    { \$set: { ThemeAvailable: true } }
);

print('✅ Theme released for gift #' + giftId + ' (source: star-gifts)');
print('   Existing NFTs stamped: ' + updateResult.modifiedCount);
print('   Saved gifts updated: ' + savedResult.modifiedCount);
print('   Center color: 0x' + centerColor.toString(16));
print('   Edge color: 0x' + edgeColor.toString(16));
print('   Pattern color: 0x' + patternColor.toString(16));
print('   Text color: 0x' + textColor.toString(16));
"

echo ""
echo "✅ Theme released successfully!"
echo "Users who own any NFT of this gift can now use it as a chat theme."
echo "Future NFTs (upgraded/transferred after this release) inherit the theme automatically."
