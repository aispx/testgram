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

// 2. Create chat themes collection with full TL schema structure
print("Creating chat_themes collection...");
db.chat_themes.deleteMany({});
db.chat_themes.insertMany([
  {
    _id: "chat-theme-🏠",
    ThemeId: NumberLong(127968),
    AccessHash: NumberLong("5000000000001"),
    Slug: "chat-theme-🏠",
    Title: "🏠 Chat Theme",
    Emoticon: "🏠",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0x3390ec,
        OutboxAccentColor: 0x4fa8f5,
        MessageColorsAnimated: true,
        MessageColors: [0x3390ec, 0x6fb1f6, 0x8ac5f8, 0xa5d8fa],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0x2b7ec1,
        MessageColorsAnimated: true,
        MessageColors: [0x2b7ec1, 0x3d8fd1, 0x4fa0e1, 0x61b1f1],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-❤️",
    ThemeId: NumberLong(10084),
    AccessHash: NumberLong("5000000000002"),
    Slug: "chat-theme-❤️",
    Title: "❤️ Chat Theme",
    Emoticon: "❤️",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0xff5da2,
        OutboxAccentColor: 0xff7ab8,
        MessageColorsAnimated: true,
        MessageColors: [0xff5da2, 0xff7ab8, 0xff97ce, 0xffb4e4],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0xd14a85,
        MessageColorsAnimated: true,
        MessageColors: [0xd14a85, 0xe15c97, 0xf16ea9, 0xff80bb],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🌙",
    ThemeId: NumberLong(127769),
    AccessHash: NumberLong("5000000000003"),
    Slug: "chat-theme-🌙",
    Title: "🌙 Chat Theme",
    Emoticon: "🌙",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0x8e7ec5,
        OutboxAccentColor: 0xa594d6,
        MessageColorsAnimated: true,
        MessageColors: [0x8e7ec5, 0xa594d6, 0xbcaae7, 0xd3c0f8],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0x7565a3,
        MessageColorsAnimated: true,
        MessageColors: [0x7565a3, 0x8676b4, 0x9787c5, 0xa898d6],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🔥",
    ThemeId: NumberLong(128293),
    AccessHash: NumberLong("5000000000004"),
    Slug: "chat-theme-🔥",
    Title: "🔥 Chat Theme",
    Emoticon: "🔥",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0xff7f50,
        OutboxAccentColor: 0xff9970,
        MessageColorsAnimated: true,
        MessageColors: [0xff7f50, 0xff9970, 0xffb390, 0xffcdb0],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0xd66a42,
        MessageColorsAnimated: true,
        MessageColors: [0xd66a42, 0xe67b53, 0xf68c64, 0xff9d75],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-⭐",
    ThemeId: NumberLong(11088),
    AccessHash: NumberLong("5000000000005"),
    Slug: "chat-theme-⭐",
    Title: "⭐ Chat Theme",
    Emoticon: "⭐",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0xffc837,
        OutboxAccentColor: 0xffd557,
        MessageColorsAnimated: true,
        MessageColors: [0xffc837, 0xffd557, 0xffe277, 0xffef97],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0xd6a72e,
        MessageColorsAnimated: true,
        MessageColors: [0xd6a72e, 0xe6b73e, 0xf6c74e, 0xffd75e],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🌸",
    ThemeId: NumberLong(127800),
    AccessHash: NumberLong("5000000000006"),
    Slug: "chat-theme-🌸",
    Title: "🌸 Chat Theme",
    Emoticon: "🌸",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0xff9eb5,
        OutboxAccentColor: 0xffb4c8,
        MessageColorsAnimated: true,
        MessageColors: [0xff9eb5, 0xffb4c8, 0xffcadb, 0xffe0ee],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0xd68599,
        MessageColorsAnimated: true,
        MessageColors: [0xd68599, 0xe695a9, 0xf6a5b9, 0xffb5c9],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🍀",
    ThemeId: NumberLong(127808),
    AccessHash: NumberLong("5000000000007"),
    Slug: "chat-theme-🍀",
    Title: "🍀 Chat Theme",
    Emoticon: "🍀",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0x34c759,
        OutboxAccentColor: 0x54d779,
        MessageColorsAnimated: true,
        MessageColors: [0x34c759, 0x54d779, 0x74e799, 0x94f7b9],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0x2ba84b,
        MessageColorsAnimated: true,
        MessageColors: [0x2ba84b, 0x3bb85b, 0x4bc86b, 0x5bd87b],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🎨",
    ThemeId: NumberLong(127912),
    AccessHash: NumberLong("5000000000008"),
    Slug: "chat-theme-🎨",
    Title: "🎨 Chat Theme",
    Emoticon: "🎨",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0x9b7ff7,
        OutboxAccentColor: 0xb199f9,
        MessageColorsAnimated: true,
        MessageColors: [0x9b7ff7, 0xb199f9, 0xc7b3fb, 0xddcdfd],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0x826bd1,
        MessageColorsAnimated: true,
        MessageColors: [0x826bd1, 0x927be1, 0xa28bf1, 0xb29bff],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🐥",
    ThemeId: NumberLong(128037),
    AccessHash: NumberLong("5000000000009"),
    Slug: "chat-theme-🐥",
    Title: "🐥 Chat Theme",
    Emoticon: "🐥",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0xffd700,
        OutboxAccentColor: 0xffe433,
        MessageColorsAnimated: true,
        MessageColors: [0xffd700, 0xffe433, 0xfff166, 0xfffe99],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0xd6b500,
        MessageColorsAnimated: true,
        MessageColors: [0xd6b500, 0xe6c510, 0xf6d520, 0xffe530],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
  },
  {
    _id: "chat-theme-🎮",
    ThemeId: NumberLong(127918),
    AccessHash: NumberLong("5000000000010"),
    Slug: "chat-theme-🎮",
    Title: "🎮 Chat Theme",
    Emoticon: "🎮",
    ForChat: true,
    Creator: false,
    Default: false,
    Settings: [
      {
        BaseTheme: "classic",
        AccentColor: 0x5b9cf5,
        OutboxAccentColor: 0x7bb2f7,
        MessageColorsAnimated: true,
        MessageColors: [0x5b9cf5, 0x7bb2f7, 0x9bc8f9, 0xbbdefb],
        Wallpaper: { Id: NumberLong(0), Dark: false, Settings: { BackgroundColor: 0xffffff, Intensity: 0 } }
      },
      {
        BaseTheme: "night",
        AccentColor: 0x4c83d1,
        MessageColorsAnimated: true,
        MessageColors: [0x4c83d1, 0x5c93e1, 0x6ca3f1, 0x7cb3ff],
        Wallpaper: { Id: NumberLong(0), Dark: true, Settings: { BackgroundColor: 0x0f0f0f, Intensity: 0 } }
      }
    ]
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
