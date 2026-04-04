namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Get all available chat <a href="https://corefork.telegram.org/api/themes">themes »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getChatThemes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetChatThemesHandler : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetChatThemes, MyTelegram.Schema.Account.IThemes>
{
    protected override Task<MyTelegram.Schema.Account.IThemes> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetChatThemes obj)
    {
        var themes = new TVector<MyTelegram.Schema.ITheme>();

        // Chat themes with full settings matching official Telegram
        // Each theme has light (BaseThemeClassic) and dark (BaseThemeNight) variants
        // Colors are chosen to match official Telegram chat themes

        // 🏠 Blue theme - default blue colors
        themes.Add(CreateChatTheme(
            emoticon: "🏠",
            lightAccent: unchecked((int)0xFF3390EC),      // Bright blue
            lightOutbox: unchecked((int)0xFF4FA8F5),      // Lighter blue for outgoing
            lightColors: new[] { unchecked((int)0xFF3390EC), unchecked((int)0xFF6FB1F6), unchecked((int)0xFF8AC5F8), unchecked((int)0xFFA5D8FA) },
            darkAccent: unchecked((int)0xFF2B7EC1),       // Darker blue
            darkColors: new[] { unchecked((int)0xFF2B7EC1), unchecked((int)0xFF3D8FD1), unchecked((int)0xFF4FA0E1), unchecked((int)0xFF61B1F1) }
        ));

        // ❤️ Pink theme - romantic pink/red
        themes.Add(CreateChatTheme(
            emoticon: "❤️",
            lightAccent: unchecked((int)0xFFFF5DA2),      // Hot pink
            lightOutbox: unchecked((int)0xFFFF7AB8),      // Lighter pink
            lightColors: new[] { unchecked((int)0xFFFF5DA2), unchecked((int)0xFFFF7AB8), unchecked((int)0xFFFF97CE), unchecked((int)0xFFFFB4E4) },
            darkAccent: unchecked((int)0xFFD14A85),       // Darker pink
            darkColors: new[] { unchecked((int)0xFFD14A85), unchecked((int)0xFFE15C97), unchecked((int)0xFFF16EA9), unchecked((int)0xFFFF80BB) }
        ));

        // 🌙 Purple theme - night purple
        themes.Add(CreateChatTheme(
            emoticon: "🌙",
            lightAccent: unchecked((int)0xFF8E7EC5),      // Soft purple
            lightOutbox: unchecked((int)0xFFA594D6),      // Lighter purple
            lightColors: new[] { unchecked((int)0xFF8E7EC5), unchecked((int)0xFFA594D6), unchecked((int)0xFFBCAAE7), unchecked((int)0xFFD3C0F8) },
            darkAccent: unchecked((int)0xFF7565A3),       // Darker purple
            darkColors: new[] { unchecked((int)0xFF7565A3), unchecked((int)0xFF8676B4), unchecked((int)0xFF9787C5), unchecked((int)0xFFA898D6) }
        ));

        // 🔥 Orange theme - warm fire colors
        themes.Add(CreateChatTheme(
            emoticon: "🔥",
            lightAccent: unchecked((int)0xFFFF7F50),      // Coral orange
            lightOutbox: unchecked((int)0xFFFF9970),      // Lighter orange
            lightColors: new[] { unchecked((int)0xFFFF7F50), unchecked((int)0xFFFF9970), unchecked((int)0xFFFFB390), unchecked((int)0xFFFFCDB0) },
            darkAccent: unchecked((int)0xFFD66A42),       // Darker orange
            darkColors: new[] { unchecked((int)0xFFD66A42), unchecked((int)0xFFE67B53), unchecked((int)0xFFF68C64), unchecked((int)0xFFFF9D75) }
        ));

        // ⭐ Yellow theme - bright star yellow
        themes.Add(CreateChatTheme(
            emoticon: "⭐",
            lightAccent: unchecked((int)0xFFFFC837),      // Golden yellow
            lightOutbox: unchecked((int)0xFFFFD557),      // Lighter yellow
            lightColors: new[] { unchecked((int)0xFFFFC837), unchecked((int)0xFFFFD557), unchecked((int)0xFFFFE277), unchecked((int)0xFFFFEF97) },
            darkAccent: unchecked((int)0xFFD6A72E),       // Darker yellow
            darkColors: new[] { unchecked((int)0xFFD6A72E), unchecked((int)0xFFE6B73E), unchecked((int)0xFFF6C74E), unchecked((int)0xFFFFD75E) }
        ));

        // 🌸 Rose theme - soft pink flower
        themes.Add(CreateChatTheme(
            emoticon: "🌸",
            lightAccent: unchecked((int)0xFFFF9EB5),      // Rose pink
            lightOutbox: unchecked((int)0xFFFFB4C8),      // Lighter rose
            lightColors: new[] { unchecked((int)0xFFFF9EB5), unchecked((int)0xFFFFB4C8), unchecked((int)0xFFFFCADB), unchecked((int)0xFFFFE0EE) },
            darkAccent: unchecked((int)0xFFD68599),       // Darker rose
            darkColors: new[] { unchecked((int)0xFFD68599), unchecked((int)0xFFE695A9), unchecked((int)0xFFF6A5B9), unchecked((int)0xFFFFB5C9) }
        ));

        // 🍀 Green theme - fresh nature green
        themes.Add(CreateChatTheme(
            emoticon: "🍀",
            lightAccent: unchecked((int)0xFF34C759),      // Fresh green
            lightOutbox: unchecked((int)0xFF54D779),      // Lighter green
            lightColors: new[] { unchecked((int)0xFF34C759), unchecked((int)0xFF54D779), unchecked((int)0xFF74E799), unchecked((int)0xFF94F7B9) },
            darkAccent: unchecked((int)0xFF2BA84B),       // Darker green
            darkColors: new[] { unchecked((int)0xFF2BA84B), unchecked((int)0xFF3BB85B), unchecked((int)0xFF4BC86B), unchecked((int)0xFF5BD87B) }
        ));

        // 🎨 Violet theme - artistic purple
        themes.Add(CreateChatTheme(
            emoticon: "🎨",
            lightAccent: unchecked((int)0xFF9B7FF7),      // Bright violet
            lightOutbox: unchecked((int)0xFFB199F9),      // Lighter violet
            lightColors: new[] { unchecked((int)0xFF9B7FF7), unchecked((int)0xFFB199F9), unchecked((int)0xFFC7B3FB), unchecked((int)0xFFDDCDFD) },
            darkAccent: unchecked((int)0xFF826BD1),       // Darker violet
            darkColors: new[] { unchecked((int)0xFF826BD1), unchecked((int)0xFF927BE1), unchecked((int)0xFFA28BF1), unchecked((int)0xFFB29BFF) }
        ));

        // 🐥 Chick theme - cute yellow
        themes.Add(CreateChatTheme(
            emoticon: "🐥",
            lightAccent: unchecked((int)0xFFFFD700),      // Gold
            lightOutbox: unchecked((int)0xFFFFE433),      // Light gold
            lightColors: new[] { unchecked((int)0xFFFFD700), unchecked((int)0xFFFFE433), unchecked((int)0xFFFFF166), unchecked((int)0xFFFFFE99) },
            darkAccent: unchecked((int)0xFFD6B500),       // Dark gold
            darkColors: new[] { unchecked((int)0xFFD6B500), unchecked((int)0xFFE6C510), unchecked((int)0xFFF6D520), unchecked((int)0xFFFFE530) }
        ));

        // 🎮 Game theme - tech blue-purple
        themes.Add(CreateChatTheme(
            emoticon: "🎮",
            lightAccent: unchecked((int)0xFF5B9CF5),      // Tech blue
            lightOutbox: unchecked((int)0xFF7BB2F7),      // Lighter tech blue
            lightColors: new[] { unchecked((int)0xFF5B9CF5), unchecked((int)0xFF7BB2F7), unchecked((int)0xFF9BC8F9), unchecked((int)0xFFBBDEFB) },
            darkAccent: unchecked((int)0xFF4C83D1),       // Darker tech blue
            darkColors: new[] { unchecked((int)0xFF4C83D1), unchecked((int)0xFF5C93E1), unchecked((int)0xFF6CA3F1), unchecked((int)0xFF7CB3FF) }
        ));

        return Task.FromResult<IThemes>(new MyTelegram.Schema.Account.TThemes
        {
            // Use timestamp-based hash to ensure client updates cache
            Hash = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Themes = themes
        });
    }

    /// <summary>
    /// Creates a chat theme with light and dark variants
    /// </summary>
    /// <param name="emoticon">Theme emoji (e.g., "🏠")</param>
    /// <param name="lightAccent">Accent color for light theme (ARGB format)</param>
    /// <param name="lightOutbox">Outgoing message color for light theme</param>
    /// <param name="lightColors">Message bubble gradient colors for light theme (2-4 colors)</param>
    /// <param name="darkAccent">Accent color for dark theme</param>
    /// <param name="darkColors">Message bubble gradient colors for dark theme</param>
    private static MyTelegram.Schema.ITheme CreateChatTheme(
        string emoticon,
        int lightAccent,
        int lightOutbox,
        int[] lightColors,
        int darkAccent,
        int[] darkColors)
    {
        var settings = new TVector<MyTelegram.Schema.IThemeSettings>();

        // Light theme variant (BaseThemeClassic)
        // Used when user has light mode enabled
        // White background (#FFFFFF) with bright accent colors
        settings.Add(new MyTelegram.Schema.TThemeSettings
        {
            BaseTheme = new MyTelegram.Schema.TBaseThemeClassic(),
            AccentColor = lightAccent,
            OutboxAccentColor = lightOutbox,
            MessageColorsAnimated = true,  // Enable gradient animation
            MessageColors = new TVector<int>(lightColors),
            // Wallpaper with light background
            Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
            {
                Id = 0,
                Settings = new MyTelegram.Schema.TWallPaperSettings
                {
                    BackgroundColor = unchecked((int)0xFFFFFFFF),  // White
                    Intensity = 0
                }
            }
        });

        // Dark theme variant (BaseThemeNight)
        // Used when user has dark mode enabled
        // Dark background (#0F0F0F) with muted accent colors
        settings.Add(new MyTelegram.Schema.TThemeSettings
        {
            BaseTheme = new MyTelegram.Schema.TBaseThemeNight(),
            AccentColor = darkAccent,
            MessageColorsAnimated = true,
            MessageColors = new TVector<int>(darkColors),
            // Wallpaper with dark background
            Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
            {
                Id = 0,
                Dark = true,
                Settings = new MyTelegram.Schema.TWallPaperSettings
                {
                    BackgroundColor = unchecked((int)0xFF0F0F0F),  // Very dark gray
                    Intensity = 0
                }
            }
        });

        return new MyTelegram.Schema.TTheme
        {
            // Flags
            ForChat = true,      // This is a chat theme (not app theme)
            Creator = false,     // Not created by user
            Default = false,     // Not the default theme

            // Identity
            Id = Math.Abs(emoticon.GetHashCode()),  // Stable ID based on emoji
            AccessHash = Random.Shared.NextInt64(),
            Slug = $"chat-theme-{emoticon}",
            Title = $"{emoticon} Chat Theme",

            // Theme data
            Emoticon = emoticon,
            Settings = settings
        };
    }
}
