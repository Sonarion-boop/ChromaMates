using MiraAPI.GameOptions;
using MiraAPI.GameOptions.OptionTypes;

namespace ChromaMates;

public enum ColorCapacityPreset
{
    Mira,
    OneHundred,
    TwoHundredFiftyTwo,
    FiveHundred,
    OneThousand,
    FifteenHundred,
    TwoThousand,
    TwentyFiveHundred,
    Full
}

public sealed class ChromaMatesOptions : AbstractOptionGroup
{
    public override string GroupName => "ChromaMates";

    public override uint GroupPriority => 1;

    public ModdedEnumOption AvailableColorPreset { get; set; } =
        new(
            "Colors Available in Lobbies",
            (int)ColorCapacityPreset.TwoHundredFiftyTwo,
            typeof(ColorCapacityPreset),
            [
                "TOU:M Set (52)",
                "100 Colors",
                "252 Colors (Default)",
                "500 Colors",
                "1,000 Colors",
                "1,500 Colors",
                "2,000 Colors",
                "2,500 Colors",
                "Full Catalog (3,003)"
            ]);
}
