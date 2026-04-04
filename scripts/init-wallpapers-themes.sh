#!/bin/bash

# Wallpapers & Themes Initialization Script
# Creates collections and sample data for testing

echo "🎨 Initializing Wallpapers and Themes data..."

cd /root/testgram/docker/compose

# Connect to MongoDB
docker-compose exec -T mongodb mongosh tg --quiet --eval '

// 1. Create wallpapers collection
print("Creating wallpapers collection...");
db.wallpapers.deleteMany({});
db.wallpapers.insertMany([
  // Gradient wallpapers
  {
    _id: "wallpaper-1000000000001",
    WallpaperId: NumberLong("1000000000001"),
    AccessHash: NumberLong("0"),
    Slug: "gradient-blue-pink",
    IsDefault: true,
    IsPattern: false,
    IsDark: false,
    DocumentId: NumberLong("0"),
    Settings: {
      BackgroundColor: 0x0077ff,
      SecondBackgroundColor: 0xff0077,
      Rotation: 135
    }
  },
  {
    _id: "wallpaper-1000000000002",
    WallpaperId: NumberLong("1000000000002"),
    AccessHash: NumberLong("0"),
    Slug: "gradient-green-cyan",
    IsDefault: true,
    IsPattern: false,
    IsDark: false,
    DocumentId: NumberLong("0"),
    Settings: {
      BackgroundColor: 0x00ff88,
      SecondBackgroundColor: 0x00ccff,
      Rotation: 45
    }
  },
  {
    _id: "wallpaper-1000000000003",
    WallpaperId: NumberLong("1000000000003"),
    AccessHash: NumberLong("0"),
    Slug: "gradient-purple-orange",
    IsDefault: true,
    IsPattern: false,
    IsDark: false,
    DocumentId: NumberLong("0"),
    Settings: {
      BackgroundColor: 0x9933ff,
      SecondBackgroundColor: 0xff9933,
      Rotation: 90
    }
  },
  // Solid colors
  {
    _id: "wallpaper-1000000000004",
    WallpaperId: NumberLong("1000000000004"),
    AccessHash: NumberLong("0"),
    Slug: "solid-dark",
    IsDefault: true,
    IsPattern: false,
    IsDark: true,
    DocumentId: NumberLong("0"),
    Settings: {
      BackgroundColor: 0x1c1c1c
    }
  },
  {
    _id: "wallpaper-1000000000005",
    WallpaperId: NumberLong("1000000000005"),
    AccessHash: NumberLong("0"),
    Slug: "solid-light",
    IsDefault: true,
    IsPattern: false,
    IsDark: false,
    DocumentId: NumberLong("0"),
    Settings: {
      BackgroundColor: 0xffffff
    }
  },
  // Freeform gradient
  {
    _id: "wallpaper-1000000000006",
    WallpaperId: NumberLong("1000000000006"),
    AccessHash: NumberLong("0"),
    Slug: "gradient-rainbow",
    IsDefault: true,
    IsPattern: false,
    IsDark: false,
    DocumentId: NumberLong("0"),
    Settings: {
      BackgroundColor: 0xff0000,
      SecondBackgroundColor: 0x00ff00,
      ThirdBackgroundColor: 0x0000ff,
      FourthBackgroundColor: 0xffff00
    }
  }
]);

// 2. Create chat themes collection
print("Creating chat_themes collection...");
db.chat_themes.deleteMany({});
db.chat_themes.insertMany([
  {
    _id: "chat-theme-home",
    Emoticon: "🏠",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0x3390ec,
      MessageColors: [0x3390ec, 0x6fb1f0],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0x3390ec,
      MessageColors: [0x3390ec, 0x6fb1f0],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-heart",
    Emoticon: "❤️",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0xff3b30,
      MessageColors: [0xff3b30, 0xff6b5e],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0xff3b30,
      MessageColors: [0xff3b30, 0xff6b5e],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-moon",
    Emoticon: "🌙",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0x8e8e93,
      MessageColors: [0x8e8e93, 0xaeaeb2],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0x8e8e93,
      MessageColors: [0x8e8e93, 0xaeaeb2],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-fire",
    Emoticon: "🔥",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0xff9500,
      MessageColors: [0xff9500, 0xffcc00],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0xff9500,
      MessageColors: [0xff9500, 0xffcc00],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-star",
    Emoticon: "⭐",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0xffcc00,
      MessageColors: [0xffcc00, 0xffee00],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0xffcc00,
      MessageColors: [0xffcc00, 0xffee00],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-flower",
    Emoticon: "🌸",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0xff2d55,
      MessageColors: [0xff2d55, 0xff6b9d],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0xff2d55,
      MessageColors: [0xff2d55, 0xff6b9d],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-leaf",
    Emoticon: "🍀",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0x34c759,
      MessageColors: [0x34c759, 0x5dd879],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0x34c759,
      MessageColors: [0x34c759, 0x5dd879],
      BackgroundColor: 0x0f0f0f
    }
  },
  {
    _id: "chat-theme-ocean",
    Emoticon: "🌊",
    Type: "emoji",
    LightTheme: {
      AccentColor: 0x007aff,
      MessageColors: [0x007aff, 0x5ac8fa],
      BackgroundColor: 0xffffff
    },
    DarkTheme: {
      AccentColor: 0x007aff,
      MessageColors: [0x007aff, 0x5ac8fa],
      BackgroundColor: 0x0f0f0f
    }
  }
]);

// 3. Create indexes
print("Creating indexes...");
db.wallpapers.createIndex({ "WallpaperId": 1 }, { unique: true });
db.wallpapers.createIndex({ "Slug": 1 }, { unique: true });
db.chat_themes.createIndex({ "Emoticon": 1 }, { unique: true });
db.user_wallpapers.createIndex({ "UserId": 1, "WallpaperId": 1 }, { unique: true });
db.user_themes.createIndex({ "UserId": 1, "ThemeId": 1 }, { unique: true });
db.user_settings.createIndex({ "UserId": 1 }, { unique: true });

print("✅ Wallpapers and Themes initialization complete!");
print("Created " + db.wallpapers.countDocuments({}) + " wallpapers");
print("Created " + db.chat_themes.countDocuments({}) + " chat themes");

'

echo "✅ Wallpapers and Themes data initialized successfully!"
echo ""
echo "Available wallpapers:"
echo "  1. gradient-blue-pink (Blue → Pink gradient)"
echo "  2. gradient-green-cyan (Green → Cyan gradient)"
echo "  3. gradient-purple-orange (Purple → Orange gradient)"
echo "  4. solid-dark (Dark solid color)"
echo "  5. solid-light (Light solid color)"
echo "  6. gradient-rainbow (4-color freeform gradient)"
echo ""
echo "Available chat themes:"
echo "  1. 🏠 Home (Blue)"
echo "  2. ❤️ Heart (Red)"
echo "  3. 🌙 Moon (Gray)"
echo "  4. 🔥 Fire (Orange)"
echo "  5. ⭐ Star (Yellow)"
echo "  6. 🌸 Flower (Pink)"
echo "  7. 🍀 Leaf (Green)"
echo "  8. 🌊 Ocean (Blue)"
echo ""
