#!/bin/bash

# Script to release a theme for a unique star gift
# Usage: ./release-gift-theme.sh <unique_gift_id> <center_color> <edge_color> <pattern_color> <text_color>

if [ "$#" -lt 2 ]; then
    echo "Usage: $0 <unique_gift_id> <center_color> [edge_color] [pattern_color] [text_color]"
    echo "Example: $0 1001 0x3390ec 0x6fb1f6 0x8ac5f8 0xffffff"
    exit 1
fi

UNIQUE_GIFT_ID=$1
CENTER_COLOR=${2:-0x3390ec}
EDGE_COLOR=${3:-0x6fb1f6}
PATTERN_COLOR=${4:-0x8ac5f8}
TEXT_COLOR=${5:-0xffffff}

echo "🎨 Releasing theme for unique gift #$UNIQUE_GIFT_ID..."

cd /root/testgram/docker/compose

# Update unique gift with theme settings
docker-compose exec -T mongodb mongosh tg --quiet --eval "
const uniqueGiftId = NumberLong('$UNIQUE_GIFT_ID');
const centerColor = $CENTER_COLOR;
const edgeColor = $EDGE_COLOR;
const patternColor = $PATTERN_COLOR;
const textColor = $TEXT_COLOR;

// Find the unique gift
const gift = db['unique-star-gifts'].findOne({ UniqueGiftId: uniqueGiftId });
if (!gift) {
    print('❌ Error: Unique gift #' + uniqueGiftId + ' not found');
    quit(1);
}

// Calculate darker colors for dark theme
function darkenColor(color) {
    const r = Math.floor(((color >> 16) & 0xFF) * 0.8);
    const g = Math.floor(((color >> 8) & 0xFF) * 0.8);
    const b = Math.floor((color & 0xFF) * 0.8);
    return (r << 16) | (g << 8) | b;
}

const darkCenterColor = darkenColor(centerColor);
const darkEdgeColor = darkenColor(edgeColor);

// Update gift with theme settings
db['unique-star-gifts'].updateOne(
    { UniqueGiftId: uniqueGiftId },
    {
        \$set: {
            ThemeAvailable: true,
            ThemeSettings: [
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
            ]
        }
    }
);

// Update saved gift to mark theme as available
db['saved-star-gifts'].updateOne(
    { UniqueGiftId: uniqueGiftId },
    { \$set: { ThemeAvailable: true } }
);

print('✅ Theme released for gift: ' + gift.Title + ' (' + gift.Slug + ')');
print('   Center color: 0x' + centerColor.toString(16));
print('   Edge color: 0x' + edgeColor.toString(16));
print('   Pattern color: 0x' + patternColor.toString(16));
print('   Text color: 0x' + textColor.toString(16));
"

echo ""
echo "✅ Theme released successfully!"
echo "Users who own this gift can now use it as a chat theme."
