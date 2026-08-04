namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// See https://core.telegram.org/api/colors
/// </summary>
public class PeerColorPaletteProvider : IPeerColorPaletteProvider, ISingletonDependency
{
    /// <summary>
    /// Palette ids 0-6 are the base colors (red, orange, violet, green, cyan, blue, pink) that
    /// every peer may use without boosting; they carry no color set, clients use their built-in one.
    /// </summary>
    private const int BaseColorIdMax = 6;

    /// <summary>Boost level required by channels/supergroups for the non-base message palettes.</summary>
    private const int MessageColorMinLevel = 1;

    /// <summary>Matches <c>channel_profile_bg_icon_level_min</c> in the app config.</summary>
    private const int ProfileColorChannelMinLevel = 7;

    /// <summary>Matches <c>group_profile_bg_icon_level_min</c> in the app config.</summary>
    private const int ProfileColorGroupMinLevel = 5;

    private static readonly IReadOnlyList<IPeerColorOption> MessageColors =
    [
        BaseOption(5), BaseOption(3), BaseOption(1), BaseOption(0), BaseOption(2), BaseOption(4), BaseOption(6),
        MessageOption(12, [3379668, 8246256], [5423103, 742548]),
        MessageOption(10, [2599184, 11000919], [11004782, 1474093]),
        MessageOption(8, [14712875, 16434484], [15511630, 12801812]),
        MessageOption(7, [14766162, 16363107], [16749440, 10039095]),
        MessageOption(9, [10510323, 16027647], [13015039, 6173128]),
        MessageOption(11, [2600142, 8579286], [4249808, 285823]),
        MessageOption(13, [14500721, 16760479], [16746150, 9320046]),
        MessageOption(14, [2391021, 15747158, 16777215], [4170494, 15024719, 16777215]),
        MessageOption(15, [14055202, 2007057, 16777215], [16748638, 3319079, 16777215]),
        MessageOption(16, [1547842, 15223359, 16777215], [6738788, 13976655, 16777215]),
        MessageOption(17, [2659503, 7324758, 16777215], [2276578, 4039232, 16777215]),
        MessageOption(18, [826035, 16756117, 16770741], [2276578, 16750456, 16767595]),
        MessageOption(19, [7821270, 16225808, 16768654], [9933311, 15889181, 16767833]),
        MessageOption(20, [1410511, 15903517, 16777215], [4040427, 15639837, 16777215]),
        MessageOption(21, [0xfc4528, 0x4fd57e, 0xf62e7b], [0x640db5, 0x498141, 0xbf5103])
    ];

    private static readonly IReadOnlyList<IPeerColorOption> ProfileColors =
    [
        ProfileOption(5, [4888278], [5935035], [7264511, 7405535], [3375297], [4682132], [9029631, 7536638]),
        ProfileOption(3, [5485111], [4825941], [7991418, 13299018], [3972130], [3371323], [7919501, 12703080]),
        ProfileOption(1, [14386489], [12745790], [16756531, 16240230], [12807972], [9723436], [16756531, 16240230]),
        ProfileOption(0, [13722204], [12211792], [16752253, 16758622], [12209223], [10241344], [16744573, 16745797]),
        ProfileOption(2, [10513887], [9792200], [16030463, 16753387], [9000906], [7426201], [15366399, 16755185]),
        ProfileOption(4, [4565185], [4102061], [5036799, 4325314], [3646897], [3702407], [4708863, 3342285]),
        ProfileOption(6, [13460119], [12079992], [16746153, 16754323], [11947138], [9717603], [16675727, 16751486]),
        ProfileOption(7, [9477803], [8358805], [12834019, 15726847], [7964822], [4412001], [10071235, 14871283]),
        ProfileOption(13, [3574481, 8246256], [5475266, 5089469], [7264511, 7405535], [5148620, 7525869], [3694988, 4557729], [9029631, 7536638]),
        ProfileOption(11, [2599184, 11000919], [4036437, 9021008], [7991418, 13299018], [4036437, 10932055], [2714179, 6262596], [7919501, 12703080]),
        ProfileOption(9, [14712875, 15842348], [13595204, 13407283], [16756531, 16240230], [13595204, 15247677], [9393455, 10580530], [16756531, 16240230]),
        ProfileOption(8, [14966882, 16363107], [13194845, 14253143], [16752253, 16758622], [13194845, 16486759], [10044227, 11294782], [16744573, 16745797]),
        ProfileOption(10, [10510323, 16027647], [9855700, 12150454], [16030463, 16753387], [9855700, 15236580], [6506129, 9588898], [15366399, 16755185]),
        ProfileOption(12, [2600142, 9234906], [4036026, 5287320], [5036799, 4325314], [3774400, 7986629], [3173500, 4102270], [4708863, 3342285]),
        ProfileOption(14, [13715826, 16760479], [11554676, 13723245], [16746153, 16754323], [12865666, 15830166], [8929632, 10900057], [16675727, 16751486]),
        ProfileOption(15, [7108740, 11384769], [6517890, 8096407], [12834019, 15726847], [7108740, 11384769], [5464174, 3688020], [10071235, 14871283])
    ];

    public IReadOnlyList<IPeerColorOption> GetMessageColorOptions() => MessageColors;

    public IReadOnlyList<IPeerColorOption> GetProfileColorOptions() => ProfileColors;

    public IPeerColorOption? GetOption(int colorId, bool forProfile)
    {
        var options = forProfile ? ProfileColors : MessageColors;

        return options.FirstOrDefault(p => p.ColorId == colorId);
    }

    public int ComputeHash(IEnumerable<IPeerColorOption> options)
    {
        var hash = 0L;
        foreach (var option in options)
        {
            hash ^= hash >> 21;
            hash ^= hash << 35;
            hash ^= hash >> 4;
            hash += option.ColorId;
        }

        return unchecked((int)hash);
    }

    /// <summary>
    /// Palette ids 0-6: no color set is sent (clients use their built-in colors) and no boost is required.
    /// </summary>
    private static TPeerColorOption BaseOption(int colorId)
    {
        return new TPeerColorOption { ColorId = colorId };
    }

    private static TPeerColorOption MessageOption(int colorId, List<int> colors, List<int> darkColors)
    {
        return new TPeerColorOption
        {
            ColorId = colorId,
            Colors = new TPeerColorSet { Colors = new TVector<int>(colors) },
            DarkColors = new TPeerColorSet { Colors = new TVector<int>(darkColors) },
            ChannelMinLevel = colorId > BaseColorIdMax ? MessageColorMinLevel : null,
            GroupMinLevel = colorId > BaseColorIdMax ? MessageColorMinLevel : null
        };
    }

    private static TPeerColorOption ProfileOption(
        int colorId,
        List<int> paletteColors,
        List<int> bgColors,
        List<int> storyColors,
        List<int> darkPaletteColors,
        List<int> darkBgColors,
        List<int> darkStoryColors)
    {
        return new TPeerColorOption
        {
            ColorId = colorId,
            Colors = new TPeerColorProfileSet
            {
                PaletteColors = new TVector<int>(paletteColors),
                BgColors = new TVector<int>(bgColors),
                StoryColors = new TVector<int>(storyColors)
            },
            DarkColors = new TPeerColorProfileSet
            {
                PaletteColors = new TVector<int>(darkPaletteColors),
                BgColors = new TVector<int>(darkBgColors),
                StoryColors = new TVector<int>(darkStoryColors)
            },
            ChannelMinLevel = ProfileColorChannelMinLevel,
            GroupMinLevel = ProfileColorGroupMinLevel
        };
    }
}
