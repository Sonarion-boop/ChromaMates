using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using HarmonyLib;
using MiraAPI.Colors;
using Reactor.Localization.Providers;
using Reactor.Localization.Utilities;
using UnityEngine;

namespace ChromaMates.Colors;

public readonly record struct RenderedPlayerColors(Color Main, Color Shadow);

internal readonly record struct ColorAnimation(
    Color32[] Frames,
    double SecondsPerFrame);

[Flags]
public enum ColorCatalogCategory
{
    None = 0,
    Vanilla = 1 << 0,
    Mira = 1 << 1,
    Spectrum = 1 << 2,
    Pastel = 1 << 3,
    Dark = 1 << 4,
    Neutral = 1 << 5,
    Pride = 1 << 6,
    Palettes = 1 << 7,
    Reserved = 1 << 8,
    Fallback = 1 << 9
}

public sealed record ColorCatalogDefinition(
    int Id,
    string Name,
    Color32 Main,
    Color32 Shadow,
    ColorCatalogCategory Categories,
    bool Animated);

public static class ColorCatalog
{
    private const double DefaultAnimationStepSeconds = 0.45d;
    private const double ExpandedFamilyAnimationStepSeconds = 0.03d;
    private const double RainbowAnimationStepSeconds = 0.07d;
    private const int RainbowFrameCount = 48;
    private const int MonochromeFrameCount = 256;
    private const double HueCycleHalfCycleSeconds = 2.42d;
    private const double MinimumPerceptualColorDistance = 2.4d;
    private const double MinimumNeutralColorDistance = 1.25d;
    private const int MaximumGenerationAttempts = 1_000_000;
    private const int StaticColorsPerSection = 327;
    private const int BalancedCatalogColorCount = 2_995;

    // Keep the original 252-color preset stable as the larger presets fill in.
    public const int PreviousColorCount = 252;
    public const int TargetColorCount = 3_003;
    public const int PaletteSlotCount = 3_008;
    public const int FirstReservedColorId = 252;
    public const int LastReservedColorId = 255;
    public const int LastSelectableColorId = 3_006;
    public const int FortegreenFallbackColorId = 3_007;
    public static readonly Color32 FortegreenMain = new(38, 166, 98, byte.MaxValue);
    public static readonly Color32 FortegreenShadow = new(18, 63, 28, byte.MaxValue);
    private static readonly ColorSelectorCategory[] StaticColorSections =
    [
        ColorSelectorCategory.Reds,
        ColorSelectorCategory.Oranges,
        ColorSelectorCategory.Yellows,
        ColorSelectorCategory.Greens,
        ColorSelectorCategory.Cyans,
        ColorSelectorCategory.Blues,
        ColorSelectorCategory.Purples,
        ColorSelectorCategory.Pinks,
        ColorSelectorCategory.Neutrals
    ];
    private static readonly HueCycleDefinition[] HueCycleDefinitions =
    [
        new(
            ColorSelectorCategory.Reds,
            "Rubicund",
            "Red",
            ["Coral", "Watermelon", "Red", "Blood"]),
        new(
            ColorSelectorCategory.Oranges,
            "Aurantia",
            "Orange",
            ["Beige", "Nacho", "Mandarin", "Orange", "Brown", "Chocolate"]),
        new(
            ColorSelectorCategory.Yellows,
            "Citrinity",
            "Yellow",
            ["Banana", "Yellow", "Gold"]),
        new(
            ColorSelectorCategory.Greens,
            "Verdancy",
            "Green",
            ["Mint", "Shimmer", "Lime", "Green", "Jungle"]),
        new(
            ColorSelectorCategory.Cyans,
            "Cyaneous",
            "Cyan",
            ["Cyan", "Turquoise", "Macau"]),
        new(
            ColorSelectorCategory.Blues,
            "Caerulean",
            "Blue",
            ["Glass", "Azure", "Sky Blue", "Blue", "Denim", "Midnight"]),
        new(
            ColorSelectorCategory.Purples,
            "Porphyry",
            "Purple",
            ["Lilac", "Violet", "Purple", "Plum"]),
        new(
            ColorSelectorCategory.Pinks,
            "Roseate",
            "Pink",
            ["Rose", "Cotton Candy", "Pink", "Magenta", "Crimson", "Maroon"])
    ];

    private static readonly Dictionary<CustomColor, ColorAnimation> AnimationsByColor = [];
    private static readonly Dictionary<int, ColorAnimation> AnimationsByColorId = [];
    private static readonly Dictionary<int, ColorCatalogDefinition> DefinitionsById = [];
    private static readonly Dictionary<int, string> FingerprintsByColorCount = [];
    private static readonly List<CustomColor> ColorsAddedByPlugin = [];
    private static readonly HashSet<CustomColor> ReservedColors = [];
    private static ColorCatalogDefinition[] _orderedDefinitions = [];
    private static readonly ColorNameCandidate[] ColorNameCandidates =
        ColorNameData.Entries.Select(entry =>
        {
            var color = new Color32(
                (byte)(entry.Rgb >> 16),
                (byte)(entry.Rgb >> 8),
                (byte)entry.Rgb,
                byte.MaxValue);
            return new ColorNameCandidate(
                ColorSelectorTabs.ClassifyStaticColor(color, CreateShadow(color)),
                entry.Name,
                NormalizeName(entry.Name),
                ToLab(color));
        }).ToArray();
    private static readonly SemanticNameCandidate[] SemanticNameCandidates =
        SemanticColorNames.Anchors.SelectMany(anchor =>
            SemanticColorNames.Tones.Select(tone =>
            {
                var name = string.IsNullOrEmpty(tone.Prefix)
                    ? anchor.Name
                    : $"{tone.Prefix} {anchor.Name}";
                var color = ApplyTone(anchor.Rgb, tone);
                return new SemanticNameCandidate(
                    anchor.Category,
                    name,
                    NormalizeName(name),
                    ToLab(color));
            })).ToArray();
    private static readonly FieldInfo ReactorHardCodedStringsField =
        AccessTools.Field(typeof(HardCodedLocalizationProvider), "_strings");
    private static bool _hasPreparedCatalog;
    private static int _rainbowColorId = -1;
    private static double _hostTimeOffsetSeconds;
    private static readonly string[] MiraColorOrder =
    [
        "Watermelon", "Chocolate", "SkyBlue", "Beige", "Magenta", "SeaGreen", "Lilac", "Olive",
        "Azure", "Plum", "Jungle", "Mint", "Chartreuse", "Macau", "Tawny", "Gold", "Snow",
        "Turquoise", "Nacho", "Blood", "Grass", "Mandarin", "Glass", "Ash", "Midnight", "Steel",
        "Silver", "Shimmer", "Crimson", "Charcoal", "Violet", "Denim", "CottonCandy", "Rainbow"
    ];
    private static readonly HashSet<string> MiraOwnedColorNames =
        new(MiraColorOrder, StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, Color32[]> RequiredAnimations =
        CreateRequiredAnimations();
    private static readonly HashSet<string> PrideColorNames = new(RequiredAnimations.Keys.Take(20),
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AnimatedPaletteNames = new(RequiredAnimations.Keys.Skip(20),
        StringComparer.OrdinalIgnoreCase);

    public static string Fingerprint { get; private set; } = string.Empty;
    public static string CompatibilityFingerprint { get; private set; } = string.Empty;
    public static bool IsFinalized { get; private set; }

    public static IReadOnlyList<CustomColor> GeneratedColors => ColorsAddedByPlugin;
    public static double SynchronizedTime =>
        Time.realtimeSinceStartupAsDouble + _hostTimeOffsetSeconds;

    public static bool IsAnimated(int colorId) => AnimationsByColorId.ContainsKey(colorId);
    public static bool IsRainbow(int colorId) => colorId == _rainbowColorId;
    public static bool IsReservedColorId(int colorId) =>
        colorId is >= FirstReservedColorId and <= LastReservedColorId;
    public static bool IsFortegreenFallbackColorId(int colorId) =>
        colorId == FortegreenFallbackColorId;
    internal static bool IsFamilyCycleName(string name) =>
        name.Equals("Monochrome", StringComparison.OrdinalIgnoreCase) ||
        HueCycleDefinitions.Any(definition =>
            definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<ColorCatalogDefinition> GetDefinitions() =>
        _orderedDefinitions;
    public static ColorCatalogDefinition? GetDefinition(int colorId) =>
        DefinitionsById.TryGetValue(colorId, out var definition) ? definition : null;
    public static int? FindNearestColorId(
        int sourceColorId,
        IEnumerable<int> candidateColorIds)
    {
        if (sourceColorId < 0 || sourceColorId >= Palette.PlayerColors.Length)
        {
            return null;
        }

        var sourceMain = ToLab(Palette.PlayerColors[sourceColorId]);
        var sourceShadow = ToLab(Palette.ShadowColors[sourceColorId]);
        return candidateColorIds
            .Where(colorId =>
                colorId >= 0 &&
                colorId < Palette.PlayerColors.Length &&
                !IsReservedColorId(colorId) &&
                !IsFortegreenFallbackColorId(colorId))
            .Select(colorId => new
            {
                ColorId = colorId,
                Distance = DistanceSquared(
                               sourceMain,
                               ToLab(Palette.PlayerColors[colorId])) +
                           0.25d * DistanceSquared(
                               sourceShadow,
                               ToLab(Palette.ShadowColors[colorId]))
            })
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.ColorId)
            .Select(candidate => (int?)candidate.ColorId)
            .FirstOrDefault();
    }

    public static IReadOnlyList<int> GetSelectableColorIds(int colorCount)
    {
        var normalizedCount = Math.Clamp(colorCount, 0, TargetColorCount);
        return Enumerable.Range(0, Palette.PlayerColors.Length)
            .Where(colorId =>
                !IsReservedColorId(colorId) &&
                !IsFortegreenFallbackColorId(colorId))
            .Take(normalizedCount)
            .ToArray();
    }

    public static string GetFingerprint(int colorCount)
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException(
                "The color catalog fingerprint was requested before finalization.");
        }

        var normalizedCount = Math.Clamp(colorCount, 0, TargetColorCount);
        if (!FingerprintsByColorCount.TryGetValue(normalizedCount, out var fingerprint))
        {
            fingerprint = CalculateFingerprint(normalizedCount);
            FingerprintsByColorCount[normalizedCount] = fingerprint;
        }
        return fingerprint;
    }

    public static string GetLiveFingerprint(int colorCount)
    {
        if (!IsFinalized)
        {
            throw new InvalidOperationException(
                "The live color catalog fingerprint was requested before finalization.");
        }

        return CalculateFingerprint(Math.Clamp(colorCount, 0, TargetColorCount));
    }

    public static void SynchronizeAnimationEpoch(double hostEpoch)
    {
        _hostTimeOffsetSeconds = hostEpoch - Time.realtimeSinceStartupAsDouble;
    }

    public static RenderedPlayerColors GetRenderedColors(int colorId, double serverTime)
    {
        if (!AnimationsByColorId.TryGetValue(colorId, out var animation) ||
            animation.Frames.Length == 0)
        {
            var safe = Math.Clamp(colorId, 0, Palette.PlayerColors.Length - 1);
            return new RenderedPlayerColors(Palette.PlayerColors[safe], Palette.ShadowColors[safe]);
        }

        var frames = animation.Frames;
        var phase = serverTime / Math.Max(0.01d, animation.SecondsPerFrame);
        var whole = Math.Floor(phase);
        var loopsForward = colorId == _rainbowColorId;
        var sequenceLength = 1;
        if (frames.Length > 1)
        {
            sequenceLength = loopsForward
                ? frames.Length
                : frames.Length * 2 - 2;
        }
        var currentStep = PositiveModulo((int)whole, sequenceLength);
        var nextStep = PositiveModulo(currentStep + 1, sequenceLength);
        var currentIndex = loopsForward
            ? currentStep
            : PingPongFrame(currentStep, frames.Length);
        var nextIndex = loopsForward
            ? nextStep
            : PingPongFrame(nextStep, frames.Length);
        var blend = (float)(phase - whole);
        var frame = (Color32)Color.Lerp(frames[currentIndex], frames[nextIndex], blend);
        return new RenderedPlayerColors(frame, CreateShadow(frame));
    }

    private static int PingPongFrame(int step, int frameCount)
    {
        if (frameCount <= 1)
        {
            return 0;
        }

        return step < frameCount ? step : frameCount * 2 - 2 - step;
    }

    private static int PositiveModulo(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    internal static void Prepare(List<CustomColor> registered)
    {
        if (_hasPreparedCatalog)
        {
            return;
        }
        _hasPreparedCatalog = true;
        var preparationTimer = Stopwatch.StartNew();

        var overflowColors = IsolateCanonicalTouCatalog(registered);
        NormalizeEcosystemDefinitions(registered);
        var ecosystemColorCount = registered.Count;
        var baseCount = Palette.PlayerColors.Length;
        RegisterAnimations(registered, baseCount);

        var originalDistribution = ProjectPreviousDistribution(registered);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Original 252-color spread: {FormatDistribution(originalDistribution)}.");

        var distribution = CountStaticDistribution(registered);
        var targetPerSection = GetStaticSectionTarget(registered, distribution);
        FillStaticSections(registered, distribution, targetPerSection);

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Balanced color-section distribution: {FormatDistribution(distribution)}.");

        TrimGeneratedColors(registered, baseCount);
        RegisterHueCycles(registered);
        OrderPluginColorsForProgressiveVisibility(registered, ecosystemColorCount);
        InsertReservedProtocolSlots(registered, baseCount, ecosystemColorCount);
        RebuildMonochrome(registered);
        RebuildRainbow(registered);
        RegisterFortegreenFallback(registered, baseCount);

        if (baseCount + registered.Count != PaletteSlotCount)
        {
            throw new InvalidOperationException(
                $"Color catalog contains {baseCount + registered.Count} palette slots; " +
                $"expected {PaletteSlotCount}.");
        }

        registered.AddRange(overflowColors);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Canonical selectable range ends at ID {LastSelectableColorId}; hidden " +
            $"Fortegreen fallback is ID {FortegreenFallbackColorId}; preserved " +
            $"{overflowColors.Count} ecosystem colors after it.");

        preparationTimer.Stop();
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Prepared the complete color catalog in {preparationTimer.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private static void RegisterAnimations(List<CustomColor> registered, int baseCount)
    {
        foreach (var (name, frames) in RequiredAnimations)
        {
            if (baseCount + registered.Count >= BalancedCatalogColorCount)
            {
                break;
            }

            var existing = FindByDisplayName(registered, name);
            if (existing != null)
            {
                AnimationsByColor[existing] =
                    new ColorAnimation(frames, DefaultAnimationStepSeconds);
                continue;
            }

            var color = new CustomColor(name, frames[0], CreateShadow(frames[0]))
            {
                ColorBrightness = CalculateLuminance(frames[0]) >= 0.52f
                    ? CustomColorBrightness.Lighter
                    : CustomColorBrightness.Darker
            };
            registered.Add(color);
            ColorsAddedByPlugin.Add(color);
            AnimationsByColor[color] =
                new ColorAnimation(frames, DefaultAnimationStepSeconds);
        }
    }

    private static int GetStaticSectionTarget(
        IReadOnlyCollection<CustomColor> registered,
        IReadOnlyDictionary<ColorSelectorCategory, int> distribution)
    {
        var animatedCount = registered.Count(color =>
            AnimationsByColor.ContainsKey(color) ||
            NormalizeName(GetDisplayName(color.Name)) == NormalizeName("Rainbow"));
        var requiredTotal = animatedCount + StaticColorsPerSection * StaticColorSections.Length;
        if (requiredTotal != BalancedCatalogColorCount)
        {
            throw new InvalidOperationException(
                $"The measured maximum requires {requiredTotal} colors, but the catalog target is " +
                $"{BalancedCatalogColorCount}.");
        }
        var overfilled = distribution
            .Where(entry => entry.Value > StaticColorsPerSection)
            .Select(entry => $"{entry.Key}={entry.Value}")
            .ToArray();
        if (overfilled.Length > 0)
        {
            throw new InvalidOperationException(
                $"Ecosystem colors already exceed the balanced target of {StaticColorsPerSection}: " +
                string.Join(", ", overfilled));
        }

        return StaticColorsPerSection;
    }

    private static void FillStaticSections(
        List<CustomColor> registered,
        IDictionary<ColorSelectorCategory, int> distribution,
        int targetPerSection)
    {
        var occupiedNames = CreateOccupiedNameSet(registered);
        FillNeutralSection(registered, distribution, targetPerSection, occupiedNames);
        var occupiedColors = Palette.PlayerColors
            .Select(ToLab)
            .Concat(registered.Select(color => ToLab(color.MainColor)))
            .ToArray();
        var occupiedLabs = new LabSpatialIndex(0.75d);
        foreach (var occupiedColor in occupiedColors)
        {
            occupiedLabs.Add(occupiedColor);
        }
        var occupiedRgb = Palette.PlayerColors
            .Concat(registered.Select(color => color.MainColor))
            .Select(PackRgb)
            .ToHashSet();

        foreach (var category in StaticColorSections.Where(category =>
                     category != ColorSelectorCategory.Neutrals))
        {
            var sequence = 1;
            while (distribution[category] < targetPerSection)
            {
                if (sequence > MaximumGenerationAttempts)
                {
                    throw new InvalidOperationException(
                        $"Unable to generate enough distinct {category} colors.");
                }

                var candidate = GenerateStaticColorCandidate(category, sequence);
                var minimumDistance = sequence switch
                {
                    < 10_000 => MinimumPerceptualColorDistance,
                    < 50_000 => 1.6d,
                    _ => 0.8d
                };
                sequence++;
                if (occupiedRgb.Contains(PackRgb(candidate)))
                {
                    continue;
                }
                if (ColorSelectorTabs.ClassifyStaticColor(
                        candidate,
                        CreateShadow(candidate)) != category)
                {
                    continue;
                }

                var candidateLab = ToLab(candidate);
                if (occupiedLabs.IsWithinDistance(candidateLab, minimumDistance))
                {
                    continue;
                }

                var generated = new CustomColor(
                    FindUnusedHumanReadableName(candidate, occupiedNames),
                    candidate,
                    CreateShadow(candidate))
                {
                    ColorBrightness = CalculateLuminance(candidate) >= 0.52f
                        ? CustomColorBrightness.Lighter
                        : CustomColorBrightness.Darker
                };
                registered.Add(generated);
                ColorsAddedByPlugin.Add(generated);
                occupiedNames.Add(NormalizeName(GetDisplayName(generated.Name)));
                occupiedRgb.Add(PackRgb(candidate));
                occupiedLabs.Add(candidateLab);
                distribution[category]++;
            }
        }
    }

    private static void FillNeutralSection(
        List<CustomColor> registered,
        IDictionary<ColorSelectorCategory, int> distribution,
        int targetPerSection,
        HashSet<string> occupiedNames)
    {
        var occupiedRgb = GetStaticColors(registered)
            .Select(color => PackRgb(color.Main))
            .ToHashSet();
        var occupiedColors = GetStaticColors(registered)
            .Where(color => ColorSelectorTabs.ClassifyStaticColor(
                color.Main,
                color.Shadow) == ColorSelectorCategory.Neutrals)
            .Select(color => ToLab(color.Main))
            .ToArray();
        var occupiedLabs = new LabSpatialIndex(MinimumNeutralColorDistance);
        foreach (var occupiedColor in occupiedColors)
        {
            occupiedLabs.Add(occupiedColor);
        }

        AddNeutralEndpoint(
            registered,
            distribution,
            occupiedNames,
            occupiedRgb,
            occupiedLabs,
            byte.MaxValue);
        AddNeutralEndpoint(
            registered,
            distribution,
            occupiedNames,
            occupiedRgb,
            occupiedLabs,
            byte.MinValue);

        var sequence = 1;
        while (distribution[ColorSelectorCategory.Neutrals] < targetPerSection)
        {
            if (sequence > MaximumGenerationAttempts)
            {
                throw new InvalidOperationException(
                    "Unable to generate enough distinct neutral colors.");
            }

            var candidate = GenerateNeutralColorCandidate(sequence++);
            if (occupiedRgb.Contains(PackRgb(candidate)))
            {
                continue;
            }
            var candidateLab = ToLab(candidate);
            if (occupiedLabs.IsWithinDistance(
                    candidateLab,
                    MinimumNeutralColorDistance))
            {
                continue;
            }

            AddGeneratedStaticColor(registered, candidate, occupiedNames);
            occupiedRgb.Add(PackRgb(candidate));
            occupiedLabs.Add(candidateLab);
            distribution[ColorSelectorCategory.Neutrals]++;
        }
    }

    private static void AddNeutralEndpoint(
        List<CustomColor> registered,
        IDictionary<ColorSelectorCategory, int> distribution,
        HashSet<string> occupiedNames,
        HashSet<int> occupiedRgb,
        LabSpatialIndex occupiedLabs,
        byte value)
    {
        var color = new Color32(value, value, value, byte.MaxValue);
        if (occupiedRgb.Contains(PackRgb(color)))
        {
            return;
        }

        AddGeneratedStaticColor(registered, color, occupiedNames);
        occupiedRgb.Add(PackRgb(color));
        occupiedLabs.Add(ToLab(color));
        distribution[ColorSelectorCategory.Neutrals]++;
    }

    private static void AddGeneratedStaticColor(
        List<CustomColor> registered,
        Color32 main,
        HashSet<string> occupiedNames)
    {
        var name = FindUnusedHumanReadableName(main, occupiedNames);
        var generated = new CustomColor(
            name,
            main,
            CreateShadow(main))
        {
            ColorBrightness = CalculateLuminance(main) >= 0.52f
                ? CustomColorBrightness.Lighter
                : CustomColorBrightness.Darker
        };
        registered.Add(generated);
        ColorsAddedByPlugin.Add(generated);
        occupiedNames.Add(NormalizeName(name));
    }

    private static int PackRgb(Color32 color) => color.r << 16 | color.g << 8 | color.b;

    private static bool SameRgb(Color32 left, Color32 right) =>
        left.r == right.r && left.g == right.g && left.b == right.b;

    private static void TrimGeneratedColors(List<CustomColor> registered, int baseCount)
    {
        while (baseCount + registered.Count > BalancedCatalogColorCount)
        {
            var removable =
                ColorsAddedByPlugin.LastOrDefault(color => !AnimationsByColor.ContainsKey(color));
            if (removable == null)
            {
                break;
            }
            ColorsAddedByPlugin.Remove(removable);
            registered.Remove(removable);
        }
    }

    private static void RegisterHueCycles(List<CustomColor> registered)
    {
        foreach (var definition in HueCycleDefinitions)
        {
            if (FindByDisplayName(registered, definition.Name) != null)
            {
                throw new InvalidOperationException(
                    $"The reserved hue-cycle name '{definition.Name}' is already registered.");
            }

            var namedAnchors = definition.PhaseNames
                .Select(phaseName => FindColorByDisplayName(registered, phaseName))
                .ToArray();
            var baseColorIndex = Array.FindIndex(
                definition.PhaseNames,
                phaseName => NormalizeName(phaseName) ==
                             NormalizeName(definition.BaseColorName));
            if (baseColorIndex < 0)
            {
                throw new InvalidOperationException(
                    $"{definition.Name} does not include its base color " +
                    $"'{definition.BaseColorName}'.");
            }

            var baseColor = namedAnchors[baseColorIndex];
            var frames = BuildHueCycleFrames(namedAnchors);
            var color = new CustomColor(
                definition.Name,
                baseColor,
                CreateShadow(baseColor))
            {
                ColorBrightness = CalculateLuminance(baseColor) >= 0.52f
                    ? CustomColorBrightness.Lighter
                    : CustomColorBrightness.Darker
            };
            registered.Add(color);
            ColorsAddedByPlugin.Add(color);
            AnimationsByColor[color] =
                new ColorAnimation(
                    frames,
                    HueCycleHalfCycleSeconds / (frames.Length - 1));
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Registered {HueCycleDefinitions.Length} named TOU:M hue-family cycles.");
    }

    private static void InsertReservedProtocolSlots(
        List<CustomColor> registered,
        int baseCount,
        int ecosystemColorCount)
    {
        if (baseCount + ecosystemColorCount > FirstReservedColorId)
        {
            throw new InvalidOperationException(
                "An ecosystem plugin already occupies ChromaMates' reserved " +
                $"{FirstReservedColorId}-{LastReservedColorId} protocol range.");
        }

        for (var colorId = FirstReservedColorId;
             colorId <= LastReservedColorId;
             colorId++)
        {
            var value = (byte)(colorId - FirstReservedColorId + 1);
            var reserved = new CustomColor(
                $"Reserved{colorId}",
                new Color32(value, value, value, byte.MaxValue),
                new Color32(0, 0, 0, byte.MaxValue))
            {
                ColorBrightness = CustomColorBrightness.Darker
            };
            ReservedColors.Add(reserved);
            registered.Insert(colorId - baseCount, reserved);
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Reserved palette IDs {FirstReservedColorId}-{LastReservedColorId} " +
            "for protocol compatibility.");
    }

    private static Color32[] BuildHueCycleFrames(IReadOnlyList<Color32> namedAnchors)
    {
        if (namedAnchors.Count == 0)
        {
            return [];
        }

        var frames = new Color32[namedAnchors.Count + 2];
        frames[0] = CreateHueCycleEndpoint(namedAnchors[0], true);
        for (var index = 0; index < namedAnchors.Count; index++)
        {
            frames[index + 1] = namedAnchors[index];
        }
        frames[^1] = CreateHueCycleEndpoint(namedAnchors[^1], false);
        return frames;
    }

    private static Color32 CreateHueCycleEndpoint(Color32 anchor, bool light)
    {
        Color.RGBToHSV(anchor, out var hue, out var saturation, out _);
        if (light)
        {
            saturation = Math.Max(0.18f, saturation * 0.55f);
            return (Color32)Color.HSVToRGB(hue, saturation, 1f);
        }

        saturation = Math.Max(0.65f, saturation);
        return (Color32)Color.HSVToRGB(hue, saturation, 0.16f);
    }

    private static void OrderPluginColorsForProgressiveVisibility(
        List<CustomColor> registered,
        int ecosystemColorCount)
    {
        var ecosystem = registered.Take(ecosystemColorCount).ToArray();
        var pluginColors = registered.Skip(ecosystemColorCount).ToArray();
        var animated = pluginColors
            .Where(AnimationsByColor.ContainsKey)
            .OrderBy(color => IsFamilyCycleName(GetDisplayName(color.Name)) ? 0 : 1)
            .ToList();
        var staticQueues = StaticColorSections.ToDictionary(
            category => category,
            category => new Queue<CustomColor>(
                OrderByProgressiveLuminanceCoverage(pluginColors
                    .Where(color =>
                        !AnimationsByColor.ContainsKey(color) &&
                        ColorSelectorTabs.ClassifyStaticColor(
                            color.MainColor,
                            color.ShadowColor) == category))));
        var visibleCounts = StaticColorSections.ToDictionary(category => category, _ => 0);
        foreach (var color in GetStaticColors(ecosystem))
        {
            visibleCounts[ColorSelectorTabs.ClassifyStaticColor(
                color.Main,
                color.Shadow)]++;
        }

        var ordered = new List<CustomColor>(pluginColors.Length);
        var animatedIndex = 0;
        while (staticQueues.Values.Any(queue => queue.Count > 0) ||
               animatedIndex < animated.Count)
        {
            if (staticQueues.Values.Any(queue => queue.Count > 0))
            {
                var category = StaticColorSections
                    .Where(candidate => staticQueues[candidate].Count > 0)
                    .OrderBy(candidate => visibleCounts[candidate])
                    .ThenBy(candidate => Array.IndexOf(StaticColorSections, candidate))
                    .First();
                ordered.Add(staticQueues[category].Dequeue());
                visibleCounts[category]++;
            }

            if (animatedIndex < animated.Count)
            {
                ordered.Add(animated[animatedIndex++]);
            }
        }

        registered.RemoveRange(ecosystemColorCount, registered.Count - ecosystemColorCount);
        registered.AddRange(ordered);
        ColorsAddedByPlugin.Clear();
        ColorsAddedByPlugin.AddRange(ordered);
    }

    private static IEnumerable<CustomColor> OrderByProgressiveLuminanceCoverage(
        IEnumerable<CustomColor> colors)
    {
        var sorted = colors
            .OrderByDescending(color => CalculateLuminance(color.MainColor))
            .ThenBy(color => color.MainColor.r)
            .ThenBy(color => color.MainColor.g)
            .ThenBy(color => color.MainColor.b)
            .ToArray();
        if (sorted.Length <= 1)
        {
            return sorted;
        }

        var indexOrder = new List<int>(sorted.Length);
        var selected = new SortedSet<int>();
        AddIndex((sorted.Length - 1) / 2);
        AddIndex(sorted.Length - 1);
        AddIndex(0);

        while (indexOrder.Count < sorted.Length)
        {
            var selectedArray = selected.ToArray();
            var largestGap = 0;
            var nextIndex = -1;
            for (var index = 1; index < selectedArray.Length; index++)
            {
                var gap = selectedArray[index] - selectedArray[index - 1];
                if (gap <= largestGap || gap <= 1)
                {
                    continue;
                }

                largestGap = gap;
                nextIndex = selectedArray[index - 1] + gap / 2;
            }

            if (nextIndex < 0)
            {
                nextIndex = Enumerable.Range(0, sorted.Length)
                    .First(index => !selected.Contains(index));
            }
            AddIndex(nextIndex);
        }

        return indexOrder.Select(index => sorted[index]);

        void AddIndex(int index)
        {
            if (selected.Add(index))
            {
                indexOrder.Add(index);
            }
        }
    }

    private static IEnumerable<(Color32 Main, Color32 Shadow)> GetStaticColors(
        IEnumerable<CustomColor> registered)
    {
        for (var i = 0; i < Palette.PlayerColors.Length; i++)
        {
            yield return (Palette.PlayerColors[i], Palette.ShadowColors[i]);
        }

        foreach (var color in registered.Where(color =>
                     !AnimationsByColor.ContainsKey(color) &&
                     !ReservedColors.Contains(color) &&
                     !GetDisplayName(color.Name).Equals(
                         "Rainbow",
                         StringComparison.OrdinalIgnoreCase)))
        {
            yield return (color.MainColor, color.ShadowColor);
        }
    }

    private static void RebuildMonochrome(IEnumerable<CustomColor> registered)
    {
        var monochrome = FindByDisplayName(registered, "Monochrome") ??
                         throw new InvalidOperationException("Monochrome was not registered.");
        var frames = Enumerable.Range(0, MonochromeFrameCount)
            .Select(index =>
            {
                var value = (byte)(byte.MaxValue - index);
                return new Color32(value, value, value, byte.MaxValue);
            })
            .ToArray();

        AnimationsByColor[monochrome] =
            new ColorAnimation(
                frames,
                ExpandedFamilyAnimationStepSeconds);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Monochrome rebuilt as a pure {frames.Length}-frame white-to-black cycle.");
    }

    private static void RebuildRainbow(IEnumerable<CustomColor> registered)
    {
        var rainbow = FindByDisplayName(registered, "Rainbow");
        if (rainbow == null)
        {
            return;
        }

        var frames = new Color32[RainbowFrameCount];
        for (var index = 0; index < frames.Length; index++)
        {
            // Frame zero is red, so wrapping the loop supplies the final red naturally.
            var hue = index / (float)frames.Length;
            frames[index] = (Color32)Color.HSVToRGB(hue, 1f, 1f);
        }

        AnimationsByColor[rainbow] = new ColorAnimation(
            frames,
            RainbowAnimationStepSeconds);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Rainbow rebuilt as an ordered {RainbowFrameCount}-frame forward loop.");
    }

    private static void RegisterFortegreenFallback(
        List<CustomColor> registered,
        int baseCount)
    {
        if (baseCount + registered.Count != FortegreenFallbackColorId)
        {
            throw new InvalidOperationException(
                $"Fortegreen must occupy palette ID {FortegreenFallbackColorId}, but the " +
                $"next available ID is {baseCount + registered.Count}.");
        }

        registered.Add(new CustomColor(
            "Fortegreen",
            FortegreenMain,
            FortegreenShadow)
        {
            ColorBrightness = CustomColorBrightness.Darker
        });
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Registered hidden Fortegreen fallback at palette ID " +
            $"{FortegreenFallbackColorId}.");
    }

    internal static void FinalizeCatalog(List<CustomColor> registered)
    {
        // Mira assigns the final IDs after Prepare, so animation lookup is finished here.
        IsFinalized = false;
        AnimationsByColorId.Clear();
        DefinitionsById.Clear();
        FingerprintsByColorCount.Clear();
        _orderedDefinitions = [];
        var baseCount = Palette.PlayerColors.Length - registered.Count;
        for (var i = 0; i < registered.Count; i++)
        {
            if (AnimationsByColor.TryGetValue(registered[i], out var animation))
            {
                AnimationsByColorId[baseCount + i] = animation;
            }
        }

        for (var i = 0; i < Palette.PlayerColors.Length; i++)
        {
            var name = GetDisplayName(Palette.ColorNames[i]);
            var main = Palette.PlayerColors[i];
            var shadow = Palette.ShadowColors[i];
            DefinitionsById[i] = new ColorCatalogDefinition(
                i,
                name,
                main,
                shadow,
                Categorize(i, name, main),
                AnimationsByColorId.ContainsKey(i));
            if (name.Equals("Rainbow", StringComparison.OrdinalIgnoreCase))
            {
                _rainbowColorId = i;
            }
        }
        _orderedDefinitions = DefinitionsById.Values
            .Where(definition => definition.Id <= LastSelectableColorId)
            .OrderBy(definition => definition.Id)
            .ToArray();
        ColorSelectorTabs.InvalidateCatalogViews();
        ValidateReservedProtocolSlots();
        ValidateBalancedFamilyPanels();
        ValidateDefaultVisibleBalance();
        ValidatePresetLuminanceCoverage(100, 0.35f);
        ValidatePresetLuminanceCoverage(PreviousColorCount, 0.50f);
        ValidateGeneratedColorNameFamilies(registered, baseCount);

        var duplicateNames = _orderedDefinitions
            .GroupBy(definition => NormalizeName(definition.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrEmpty(group.Key) && group.Count() > 1)
            .Select(group => $"{group.First().Name} [{string.Join(",", group.Select(item => item.Id))}]")
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Color catalog contains repeated display names: {string.Join("; ", duplicateNames)}");
        }

        var invalidNames = _orderedDefinitions
            .Where(definition => CountWords(definition.Name) is < 1 or > 2)
            .Select(definition => $"{definition.Name} [{definition.Id}]")
            .ToArray();
        if (invalidNames.Length > 0)
        {
            throw new InvalidOperationException(
                "Every color name must contain one or two words: " +
                string.Join("; ", invalidNames));
        }

        IsFinalized = true;
        Fingerprint = GetFingerprint(TargetColorCount);
        CompatibilityFingerprint = GetFingerprint(
            Math.Min(ColorAvailability.MiraColorCount, TargetColorCount));
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Color catalog finalized with {TargetColorCount} selectable colors across " +
            $"{Palette.PlayerColors.Length} palette slots, " +
            $"fingerprint {Fingerprint}, and 52-color compatibility fingerprint " +
            $"{CompatibilityFingerprint}.");
    }

    private static string CalculateFingerprint(int colorCount)
    {
        var material = new StringBuilder();
        material.Append("ChromaMates/")
            .Append(typeof(ChromaMatesPlugin).Assembly.GetName().Version?.ToString() ?? "unknown")
            .Append("/protocol=").Append(ChromaMatesPlugin.NetworkProtocolVersion)
            .Append("/selectable=").Append(TargetColorCount)
            .Append("/slots=").Append(PaletteSlotCount)
            .Append("/reserved=").Append(FirstReservedColorId).Append('-').Append(LastReservedColorId)
            .Append("/fallback=").Append(FortegreenFallbackColorId).Append(':')
            .Append(FortegreenMain.r).Append(',')
            .Append(FortegreenMain.g).Append(',')
            .Append(FortegreenMain.b).Append('/')
            .Append(FortegreenShadow.r).Append(',')
            .Append(FortegreenShadow.g).Append(',')
            .Append(FortegreenShadow.b)
            .Append("/limit=").Append(colorCount).Append(';');
        foreach (var i in GetSelectableColorIds(colorCount))
        {
            var main = Palette.PlayerColors[i];
            var shadow = Palette.ShadowColors[i];
            material.Append(i).Append(':')
                .Append(GetFingerprintNameKey(Palette.ColorNames[i])).Append(':')
                .Append(main.r).Append(',').Append(main.g).Append(',').Append(main.b).Append('/')
                .Append(shadow.r).Append(',').Append(shadow.g).Append(',').Append(shadow.b);
            if (AnimationsByColorId.TryGetValue(i, out var animation))
            {
                material.Append('@')
                    .Append(animation.SecondsPerFrame.ToString("R", CultureInfo.InvariantCulture))
                    .Append('[');
                foreach (var frame in animation.Frames)
                {
                    material.Append(frame.r).Append(',')
                        .Append(frame.g).Append(',')
                        .Append(frame.b).Append('/');
                }
                material.Append(']');
            }
            material.Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    private static string GetFingerprintNameKey(StringNames name)
    {
        if (ReactorHardCodedStringsField.GetValue(null) is Dictionary<StringNames, string> strings &&
            strings.TryGetValue(name, out var hardCoded))
        {
            return hardCoded;
        }

        return ((int)name).ToString(CultureInfo.InvariantCulture);
    }

    private static void ValidateReservedProtocolSlots()
    {
        var reservedIds = Enumerable.Range(
                FirstReservedColorId,
                LastReservedColorId - FirstReservedColorId + 1)
            .ToArray();
        if (reservedIds.Any(colorId =>
                !DefinitionsById.TryGetValue(colorId, out var definition) ||
                !definition.Categories.HasFlag(ColorCatalogCategory.Reserved)))
        {
            throw new InvalidOperationException(
                $"Palette IDs {FirstReservedColorId}-{LastReservedColorId} " +
                "must remain reserved.");
        }

        var defaultIds = GetSelectableColorIds(PreviousColorCount);
        var fullIds = GetSelectableColorIds(TargetColorCount);
        var validFallback =
            DefinitionsById.TryGetValue(FortegreenFallbackColorId, out var fallback) &&
            fallback.Categories.HasFlag(ColorCatalogCategory.Fallback) &&
            SameRgb(fallback.Main, FortegreenMain) &&
            SameRgb(fallback.Shadow, FortegreenShadow);
        if (defaultIds.Count != PreviousColorCount ||
            defaultIds[^1] != FirstReservedColorId - 1 ||
            fullIds.Count != TargetColorCount ||
            fullIds.Any(IsReservedColorId) ||
            fullIds.Any(IsFortegreenFallbackColorId) ||
            fullIds[^1] != LastSelectableColorId ||
            !validFallback)
        {
            throw new InvalidOperationException(
                "Selectable color counts do not correctly skip the reserved protocol range.");
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Protocol reservation verified: {TargetColorCount} selectable colors " +
            $"skip IDs {FirstReservedColorId}-{LastReservedColorId}, with hidden " +
            $"Fortegreen fallback at ID {FortegreenFallbackColorId}.");
    }

    private static void ValidateBalancedFamilyPanels()
    {
        foreach (var category in StaticColorSections)
        {
            var staticDefinitions = DefinitionsById.Values
                .Where(definition =>
                    definition.Id <= LastSelectableColorId &&
                    !definition.Categories.HasFlag(ColorCatalogCategory.Reserved) &&
                    !definition.Animated &&
                    ColorSelectorTabs.ClassifyStaticColor(
                        definition.Main,
                        definition.Shadow) == category)
                .OrderByDescending(definition => CalculateLuminance(definition.Main))
                .ThenBy(definition => definition.Main.r)
                .ThenBy(definition => definition.Main.g)
                .ThenBy(definition => definition.Main.b)
                .ToArray();
            if (staticDefinitions.Length != StaticColorsPerSection)
            {
                throw new InvalidOperationException(
                    $"{category} contains {staticDefinitions.Length} static colors; " +
                    $"expected {StaticColorsPerSection}.");
            }

            if (category == ColorSelectorCategory.Neutrals)
            {
                var pureWhite =
                    new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);
                var pureBlack =
                    new Color32(byte.MinValue, byte.MinValue, byte.MinValue, byte.MaxValue);
                if (!SameRgb(staticDefinitions[0].Main, pureWhite) ||
                    !SameRgb(staticDefinitions[^1].Main, pureBlack))
                {
                    throw new InvalidOperationException(
                        "Neutrals must begin with pure white and end with pure black.");
                }
            }

            var cycleName = GetFamilyCycleName(category);
            var cycle = DefinitionsById.Values.Single(definition =>
                definition.Id <= LastSelectableColorId &&
                definition.Name.Equals(cycleName, StringComparison.OrdinalIgnoreCase));
            HueCycleDefinition? hueDefinition =
                category == ColorSelectorCategory.Neutrals
                ? null
                : HueCycleDefinitions.Single(definition =>
                    definition.Category == category);
            var expectedFrameCount = hueDefinition.HasValue
                ? hueDefinition.Value.PhaseNames.Length + 2
                : MonochromeFrameCount;
            if (!cycle.Animated ||
                !AnimationsByColorId.TryGetValue(cycle.Id, out var animation) ||
                animation.Frames.Length != expectedFrameCount)
            {
                throw new InvalidOperationException(
                    $"{cycleName} must contain {expectedFrameCount} ordered {category} frames.");
            }
            if (category != ColorSelectorCategory.Neutrals &&
                ColorSelectorTabs.ClassifyStaticColor(cycle.Main, cycle.Shadow) != category)
            {
                throw new InvalidOperationException(
                    $"{cycleName} is not assigned to its {category} panel.");
            }

            if (category == ColorSelectorCategory.Neutrals)
            {
                if (animation.Frames.Length != MonochromeFrameCount ||
                    animation.Frames.Select((frame, index) =>
                        frame.r == byte.MaxValue - index &&
                        frame.g == frame.r &&
                        frame.b == frame.r).Any(valid => !valid))
                {
                    throw new InvalidOperationException(
                        "Monochrome must be a pure white-to-black grayscale cycle.");
                }
            }
            else
            {
                var expectedFrames = hueDefinition!.Value.PhaseNames
                    .Select(FindFinalizedColorByDisplayName)
                    .ToArray();
                var expectedBase = FindFinalizedColorByDisplayName(
                    hueDefinition.Value.BaseColorName);
                var actualAnchors = animation.Frames
                    .Skip(1)
                    .Take(expectedFrames.Length)
                    .ToArray();
                if (!SameRgb(cycle.Main, expectedBase) ||
                    animation.Frames.Distinct(new Color32Comparer()).Count() !=
                    expectedFrameCount ||
                    !actualAnchors
                        .Zip(expectedFrames, SameRgb)
                        .All(matches => matches) ||
                    CalculateLuminance(animation.Frames[0]) <=
                    CalculateLuminance(expectedFrames[0]) ||
                    CalculateLuminance(animation.Frames[^1]) >=
                    CalculateLuminance(expectedFrames[^1]))
                {
                    throw new InvalidOperationException(
                        $"{cycleName} does not span light to dark through its " +
                        "prescribed TOU:M interpolation anchors.");
                }
            }

            Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
                $"{category} panel verified with {StaticColorsPerSection} static colors plus " +
                $"{cycleName} ({expectedFrameCount} ordered keyframes).");
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"All {StaticColorSections.Length} shade-family panels are balanced at " +
            $"{StaticColorsPerSection + 1} entries each.");
    }

    private static void ValidatePresetLuminanceCoverage(
        int selectableColorCount,
        float minimumRange)
    {
        var visibleIds = GetSelectableColorIds(selectableColorCount).ToHashSet();
        var ranges = new List<string>();
        foreach (var category in StaticColorSections)
        {
            var luminances = DefinitionsById.Values
                .Where(definition =>
                    visibleIds.Contains(definition.Id) &&
                    !definition.Animated &&
                    !definition.Categories.HasFlag(ColorCatalogCategory.Reserved) &&
                    ColorSelectorTabs.ClassifyStaticColor(
                        definition.Main,
                        definition.Shadow) == category)
                .Select(definition => CalculateLuminance(definition.Main))
                .ToArray();
            if (luminances.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The {selectableColorCount}-color preset has no {category} colors.");
            }

            var darkest = luminances.Min();
            var lightest = luminances.Max();
            if (lightest - darkest < minimumRange)
            {
                throw new InvalidOperationException(
                    $"The {selectableColorCount}-color preset's {category} tab only spans " +
                    $"luminance {darkest:F2}-{lightest:F2}; expected at least " +
                    $"{minimumRange:F2} of light-to-dark variation.");
            }
            ranges.Add($"{category}={darkest:F2}-{lightest:F2}");
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"{selectableColorCount}-color light-to-dark coverage verified: " +
            string.Join(", ", ranges) + ".");
    }

    private static void ValidateGeneratedColorNameFamilies(
        IReadOnlyList<CustomColor> registered,
        int baseCount)
    {
        var knownNameCategories = SemanticNameCandidates
            .Select(candidate => (candidate.NormalizedName, candidate.Category))
            .Concat(ColorNameCandidates.Select(candidate =>
                (candidate.NormalizedName, candidate.Category)))
            .GroupBy(candidate => candidate.NormalizedName)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.Category).ToHashSet());
        var pluginColors = ColorsAddedByPlugin.ToHashSet();
        var generatedStaticIds = registered
            .Select((color, index) => (Color: color, Id: baseCount + index))
            .Where(entry =>
                pluginColors.Contains(entry.Color) &&
                !AnimationsByColor.ContainsKey(entry.Color))
            .Select(entry => entry.Id)
            .ToHashSet();
        var checkedNames = 0;
        foreach (var definition in DefinitionsById.Values.Where(definition =>
                     generatedStaticIds.Contains(definition.Id)))
        {
            var normalizedName = NormalizeName(definition.Name);
            if (!knownNameCategories.TryGetValue(normalizedName, out var expectedCategories))
            {
                continue;
            }

            checkedNames++;
            var actualCategory = ColorSelectorTabs.ClassifyStaticColor(
                definition.Main,
                definition.Shadow);
            if (!expectedCategories.Contains(actualCategory))
            {
                throw new InvalidOperationException(
                    $"Color '{definition.Name}' is categorized as {actualCategory}, but its " +
                    "real-world reference belongs to " +
                    string.Join(" or ", expectedCategories) + ".");
            }
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Verified the hue-family meaning of {checkedNames} generated color names.");
    }

    private static void ValidateDefaultVisibleBalance()
    {
        var distribution = StaticColorSections.ToDictionary(category => category, _ => 0);
        foreach (var definition in DefinitionsById.Values.Where(definition =>
                     definition.Id < PreviousColorCount &&
                     !definition.Animated &&
                     !definition.Categories.HasFlag(ColorCatalogCategory.Pride) &&
                     !definition.Categories.HasFlag(ColorCatalogCategory.Palettes)))
        {
            distribution[ColorSelectorTabs.ClassifyStaticColor(
                definition.Main,
                definition.Shadow)]++;
        }

        var smallest = distribution.Values.Min();
        var largest = distribution.Values.Max();
        if (largest - smallest > 1)
        {
            throw new InvalidOperationException(
                "The default 252-color preset is not evenly distributed: " +
                FormatDistribution(distribution));
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Default 252-color distribution verified: {FormatDistribution(distribution)}.");
    }

    private static string GetFamilyCycleName(ColorSelectorCategory category) =>
        category == ColorSelectorCategory.Neutrals
            ? "Monochrome"
            : HueCycleDefinitions.Single(definition => definition.Category == category).Name;

    private static ColorCatalogCategory Categorize(int id, string name, Color32 main)
    {
        if (IsFortegreenFallbackColorId(id))
        {
            return ColorCatalogCategory.Fallback;
        }
        if (IsReservedColorId(id))
        {
            return ColorCatalogCategory.Reserved;
        }
        if (id < 18)
        {
            return ColorCatalogCategory.Vanilla;
        }
        if (PrideColorNames.Contains(name))
        {
            return ColorCatalogCategory.Pride;
        }
        if (AnimatedPaletteNames.Contains(name))
        {
            return ColorCatalogCategory.Palettes;
        }
        if (MiraOwnedColorNames.Contains(name))
        {
            return ColorCatalogCategory.Mira;
        }

        var max = Math.Max(main.r, Math.Max(main.g, main.b));
        var min = Math.Min(main.r, Math.Min(main.g, main.b));
        var saturation = max == 0 ? 0f : (max - min) / (float)max;
        var category = saturation < 0.12f ? ColorCatalogCategory.Neutral : ColorCatalogCategory.Spectrum;
        if (CalculateLuminance(main) < 0.32f)
        {
            category |= ColorCatalogCategory.Dark;
        }
        if (CalculateLuminance(main) > 0.72f && saturation < 0.58f)
        {
            category |= ColorCatalogCategory.Pastel;
        }
        return category;
    }

    private static Dictionary<string, Color32[]> CreateRequiredAnimations() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Pride"] = Frames("#E40303", "#FF8C00", "#FFED00", "#008026", "#24408E", "#732982"),
            ["Progress"] = Frames("#000000", "#613915", "#74D7EE", "#FFAFC8", "#FFFFFF", "#E40303", "#FF8C00", "#FFED00", "#008026", "#24408E", "#732982"),
            ["Trans"] = Frames("#5BCEFA", "#F5A9B8", "#FFFFFF", "#F5A9B8", "#5BCEFA"),
            ["Nonbinary"] = Frames("#FCF434", "#FFFFFF", "#9C59D1", "#2C2C2C"),
            ["Bi"] = Frames("#D60270", "#9B4F96", "#0038A8"),
            ["Pan"] = Frames("#FF218C", "#FFD800", "#21B1FF"),
            ["Lesbian"] = Frames("#D52D00", "#EF7627", "#FF9A56", "#FFFFFF", "#D162A4", "#B55690", "#A30262"),
            ["Achillean"] = Frames("#078D70", "#26CEAA", "#98E8C1", "#FFFFFF", "#7BADE2", "#5049CC", "#3D1A78"),
            ["Ace"] = Frames("#000000", "#A3A3A3", "#FFFFFF", "#800080"),
            ["Aro"] = Frames("#3DA542", "#A7D379", "#FFFFFF", "#A9A9A9", "#000000"),
            ["Genderfluid"] = Frames("#FF76A4", "#FFFFFF", "#C011D7", "#000000", "#2F3CBE"),
            ["Genderqueer"] = Frames("#B57EDC", "#FFFFFF", "#4A8123"),
            ["Agender"] = Frames("#000000", "#B9B9B9", "#FFFFFF", "#B8F483", "#FFFFFF", "#B9B9B9", "#000000"),
            ["Intersex"] = Frames("#FFD800", "#7902AA", "#FFD800"),
            ["Poly"] = Frames("#F61CB9", "#07D569", "#1C92F6"),
            ["Omni"] = Frames("#FF9CCD", "#FF53BF", "#200044", "#6760FE", "#8EA6FF"),
            ["Demisexual"] = Frames("#000000", "#FFFFFF", "#6E0071", "#D3D3D3"),
            ["Demiromantic"] = Frames("#000000", "#FFFFFF", "#3DA542", "#D3D3D3"),
            ["Bigender"] = Frames("#C479A2", "#EDA5CD", "#D5C7E8", "#FFFFFF", "#D5C7E8", "#9AC7E8", "#6D82D1"),
            ["Abro"] = Frames("#75CA92", "#B3E4C7", "#FFFFFF", "#E695B5", "#D9446E"),
            ["Vaporwave"] = Frames("#FF71CE", "#01CDFE", "#05FFA1", "#B967FF", "#FFFB96"),
            ["Synthwave"] = Frames("#2B1B5A", "#7B2CBF", "#C77DFF", "#FF4D6D", "#FF9E00"),
            ["Sunset"] = Frames("#2D1E5F", "#B23A48", "#F06C5E", "#FFA552", "#FFE66D"),
            ["Sunrise"] = Frames("#1B3A4B", "#4D6CFA", "#FF99C8", "#FCF6BD", "#FFFFFF"),
            ["Ocean"] = Frames("#03045E", "#0077B6", "#00B4D8", "#90E0EF", "#CAF0F8"),
            ["Forest"] = Frames("#081C15", "#1B4332", "#2D6A4F", "#52B788", "#95D5B2"),
            ["Fire"] = Frames("#370617", "#9D0208", "#DC2F02", "#F48C06", "#FFBA08"),
            ["Ice"] = Frames("#E0FBFC", "#C2DFE3", "#9DB4C0", "#5C6B73", "#253237"),
            ["Aurora"] = Frames("#03045E", "#00B4D8", "#80FFDB", "#72EFDD", "#C77DFF"),
            ["Pastel"] = Frames("#FFADAD", "#FFD6A5", "#FDFFB6", "#CAFFBF", "#9BF6FF", "#A0C4FF", "#BDB2FF", "#FFC6FF"),
            ["Monochrome"] = Frames("#050505", "#333333", "#666666", "#999999", "#CCCCCC", "#FFFFFF"),
            ["Galaxy"] = Frames("#09001F", "#250052", "#5C16C5", "#B14AED", "#FF78D1"),
            ["Candy"] = Frames("#FF4FA3", "#FF8ED4", "#FFF0F7", "#78E6FF", "#4CB8FF"),
            ["Tropical"] = Frames("#FF5A5F", "#FFCA3A", "#8AC926", "#00B4D8", "#6A4C93"),
            ["Ember"] = Frames("#240000", "#7F0909", "#D93416", "#FF7B00", "#FFD166"),
            ["Glacier"] = Frames("#F0FFFF", "#BDEFFF", "#78D7FF", "#3D8BFF", "#2447A8"),
            ["Meadow"] = Frames("#173B1A", "#3E7C35", "#78B84B", "#D0DF55", "#FFF3A3"),
            ["Neon"] = Frames("#FF00A8", "#8A2BE2", "#006BFF", "#00F5D4", "#C7FF00"),
            ["Retro"] = Frames("#2B2D42", "#D96C75", "#F2CC8F", "#81B29A", "#3D405B"),
            ["Sorbet"] = Frames("#FF9AA2", "#FFB7B2", "#FFDAC1", "#E2F0CB", "#B5EAD7", "#C7CEEA"),
            ["Twilight"] = Frames("#0B1026", "#26335D", "#6D4C8D", "#C06C84", "#F6B17A"),
            ["Storm"] = Frames("#111827", "#334155", "#64748B", "#94A3B8", "#D7E3F4"),
            ["Desert"] = Frames("#5F3B24", "#9C6644", "#C98E5B", "#E9C46A", "#F4E1B6"),
            ["Lagoon"] = Frames("#012A4A", "#014F86", "#2A6F97", "#2C7DA0", "#61A5C2", "#A9D6E5"),
            ["Blossom"] = Frames("#5A1838", "#A83A67", "#E46C9A", "#FFB0C8", "#FFF0F5"),
            ["Arcade"] = Frames("#1B003A", "#6B00B6", "#D100D1", "#FF2957", "#FFD319"),
            ["Cosmic"] = Frames("#050816", "#16235A", "#5946B2", "#B85AD1", "#F5A3FF"),
            ["Voltage"] = Frames("#050505", "#163300", "#55A630", "#AACC00", "#FFFF3F"),
            ["Harvest"] = Frames("#4A1C00", "#9C2C00", "#D95D00", "#F6AE2D", "#FFE8A3"),
            ["Lunar"] = Frames("#080B1A", "#242A45", "#596275", "#A7B0C0", "#F4F7FF"),
            ["Prism"] = Frames("#FF1744", "#FF9100", "#FFEA00", "#00E676", "#00B0FF", "#651FFF", "#D500F9")
        };

    private static Color32 GenerateStaticColorCandidate(int index)
    {
        var hue = (index * 0.61803398875f) % 1f;
        var saturationBand = index % 4;
        var valueBand = index % 5;
        var saturation = 0.34f + saturationBand * 0.16f;
        var value = 0.38f + valueBand * 0.135f;
        return (Color32)Color.HSVToRGB(hue, Math.Clamp(saturation, 0f, 0.92f), Math.Clamp(value, 0f, 1f));
    }

    private static Color32 GenerateStaticColorCandidate(
        ColorSelectorCategory category,
        int index)
    {
        var (minimumHue, maximumHue) = category switch
        {
            ColorSelectorCategory.Reds => (-15f, 15f),
            ColorSelectorCategory.Oranges => (15f, 45f),
            ColorSelectorCategory.Yellows => (45f, 70f),
            ColorSelectorCategory.Greens => (70f, 165f),
            ColorSelectorCategory.Cyans => (165f, 195f),
            ColorSelectorCategory.Blues => (195f, 255f),
            ColorSelectorCategory.Purples => (255f, 295f),
            ColorSelectorCategory.Pinks => (295f, 345f),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
        var hueDegrees = minimumHue +
                         (maximumHue - minimumHue) * Halton(index, 2);
        if (hueDegrees < 0f)
        {
            hueDegrees += 360f;
        }
        var saturation = 0.18f + Halton(index, 3) * 0.78f;
        var value = 0.16f + Halton(index, 5) * 0.84f;
        return (Color32)Color.HSVToRGB(hueDegrees / 360f, saturation, value);
    }

    private static float Halton(int index, int radix)
    {
        var result = 0f;
        var fraction = 1f / radix;
        while (index > 0)
        {
            result += fraction * (index % radix);
            index /= radix;
            fraction /= radix;
        }
        return result;
    }

    private static Color32 GenerateNeutralColorCandidate(int index)
    {
        var hue = (index * 0.61803398875f) % 1f;
        var saturation = 0.012f + index % 7 * 0.014f;
        var value = 0.06f + ((index * 73) % 239) / 238f * 0.90f;
        return (Color32)Color.HSVToRGB(hue, saturation, value);
    }

    private static string FindUnusedHumanReadableName(
        Color32 candidate,
        HashSet<string> occupiedNames)
    {
        var candidateLab = ToLab(candidate);
        var category = ColorSelectorTabs.ClassifyStaticColor(
            candidate,
            CreateShadow(candidate));
        string? semanticName = null;
        var semanticDistance = double.MaxValue;
        foreach (var entry in SemanticNameCandidates.Where(entry =>
                     entry.Category == category))
        {
            if (occupiedNames.Contains(entry.NormalizedName))
            {
                continue;
            }

            var distance = DistanceSquared(candidateLab, entry.Lab);
            if (distance < semanticDistance ||
                Math.Abs(distance - semanticDistance) < double.Epsilon &&
                string.Compare(
                    entry.Name,
                    semanticName,
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                semanticName = entry.Name;
                semanticDistance = distance;
            }
        }

        if (semanticName != null)
        {
            return semanticName;
        }

        ColorNameCandidate? best = null;
        var bestDistance = double.MaxValue;
        foreach (var entry in ColorNameCandidates.Where(entry =>
                     entry.Category == category))
        {
            if (occupiedNames.Contains(entry.NormalizedName))
            {
                continue;
            }

            var distance = DistanceSquared(candidateLab, entry.Lab);
            if (distance < bestDistance ||
                Math.Abs(distance - bestDistance) < double.Epsilon &&
                best.HasValue &&
                string.Compare(
                    entry.Name,
                    best.Value.Name,
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                best = entry;
                bestDistance = distance;
            }
        }

        return best?.Name ??
               throw new InvalidOperationException(
                   $"The real-world color-name space for {category} is exhausted.");
    }

    private static Color32 ApplyTone(uint packedRgb, SemanticColorTone tone)
    {
        var anchor = new Color32(
            (byte)(packedRgb >> 16),
            (byte)(packedRgb >> 8),
            (byte)packedRgb,
            byte.MaxValue);
        Color.RGBToHSV(anchor, out var hue, out var saturation, out var value);
        saturation = Mathf.Clamp01(saturation * tone.SaturationScale);
        value = Mathf.Clamp01(value * tone.ValueScale + tone.ValueOffset);
        return (Color32)Color.HSVToRGB(hue, saturation, value);
    }

    private static int CountWords(string name) =>
        name.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private static Dictionary<ColorSelectorCategory, int> CountStaticDistribution(
        IEnumerable<CustomColor> registered)
    {
        var distribution = StaticColorSections.ToDictionary(category => category, _ => 0);
        for (var i = 0; i < Palette.PlayerColors.Length; i++)
        {
            var main = Palette.PlayerColors[i];
            var shadow = Palette.ShadowColors[i];
            distribution[ColorSelectorTabs.ClassifyStaticColor(main, shadow)]++;
        }

        foreach (var color in registered)
        {
            if (AnimationsByColor.ContainsKey(color) ||
                NormalizeName(GetDisplayName(color.Name)) == NormalizeName("Rainbow"))
            {
                continue;
            }

            distribution[ColorSelectorTabs.ClassifyStaticColor(color.MainColor, color.ShadowColor)]++;
        }

        return distribution;
    }

    private static Dictionary<ColorSelectorCategory, int> ProjectPreviousDistribution(
        IEnumerable<CustomColor> registered)
    {
        var registeredArray = registered.ToArray();
        var distribution = CountStaticDistribution(registeredArray);
        var occupied = Palette.PlayerColors
            .Concat(registeredArray.Select(color => color.MainColor))
            .ToList();
        var occupiedLabs = occupied.Select(ToLab).ToList();
        var sequence = 1;
        while (occupied.Count < PreviousColorCount)
        {
            var candidate = GenerateStaticColorCandidate(sequence++);
            var candidateLab = ToLab(candidate);
            if (occupiedLabs.Any(existing =>
                    Distance(candidateLab, existing) < MinimumPerceptualColorDistance))
            {
                continue;
            }

            occupied.Add(candidate);
            occupiedLabs.Add(candidateLab);
            distribution[ColorSelectorTabs.ClassifyStaticColor(
                candidate,
                CreateShadow(candidate))]++;
        }

        return distribution;
    }

    private static string FormatDistribution(
        IReadOnlyDictionary<ColorSelectorCategory, int> distribution) =>
        string.Join(", ",
            StaticColorSections.Select(category => $"{category}={distribution[category]}"));

    private static List<CustomColor> IsolateCanonicalTouCatalog(
        List<CustomColor> registered)
    {
        var originalCount = registered.Count;
        var remaining = registered.ToList();
        var canonicalTouColors = new List<CustomColor>(MiraColorOrder.Length);
        foreach (var requiredName in MiraColorOrder)
        {
            var normalizedRequiredName = NormalizeName(requiredName);
            var match = remaining
                .Where(color =>
                    NormalizeName(GetDisplayName(color.Name)) == normalizedRequiredName)
                .OrderBy(color => color.MainColor.r)
                .ThenBy(color => color.MainColor.g)
                .ThenBy(color => color.MainColor.b)
                .ThenBy(color => color.ShadowColor.r)
                .ThenBy(color => color.ShadowColor.g)
                .ThenBy(color => color.ShadowColor.b)
                .FirstOrDefault();
            if (match == null)
            {
                throw new InvalidOperationException(
                    $"The required TOU:M base color '{requiredName}' was not registered. " +
                    "ChromaMates cannot construct a synchronized catalog without the same " +
                    "TOU:M color baseline on every client.");
            }

            canonicalTouColors.Add(match);
            remaining.Remove(match);
        }

        var overflow = remaining
            .OrderBy(color => NormalizeName(GetDisplayName(color.Name)), StringComparer.Ordinal)
            .ThenBy(color => color.MainColor.r)
            .ThenBy(color => color.MainColor.g)
            .ThenBy(color => color.MainColor.b)
            .ThenBy(color => color.ShadowColor.r)
            .ThenBy(color => color.ShadowColor.g)
            .ThenBy(color => color.ShadowColor.b)
            .ToList();
        registered.Clear();
        registered.AddRange(canonicalTouColors);

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Isolated the fixed {Palette.PlayerColors.Length + canonicalTouColors.Count}-color " +
            $"vanilla/TOU:M baseline from {originalCount} registered ecosystem colors.");
        return overflow;
    }

    private static void NormalizeEcosystemDefinitions(List<CustomColor> colors)
    {
        var seen = new HashSet<(byte, byte, byte, byte, byte, byte)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unavailableNames = CreateOccupiedNameSet(colors);
        for (var i = 0; i < Palette.PlayerColors.Length && i < Palette.ShadowColors.Length; i++)
        {
            var main = Palette.PlayerColors[i];
            var shadow = Palette.ShadowColors[i];
            seen.Add((main.r, main.g, main.b, shadow.r, shadow.g, shadow.b));
            var vanillaName = NormalizeName(GetDisplayName(Palette.ColorNames[i]));
            if (!string.IsNullOrEmpty(vanillaName))
            {
                names.Add(vanillaName);
            }
        }

        var colorIndex = 0;
        while (colorIndex < colors.Count)
        {
            var color = colors[colorIndex];
            var key = (color.MainColor.r, color.MainColor.g, color.MainColor.b,
                color.ShadowColor.r, color.ShadowColor.g, color.ShadowColor.b);
            if (!seen.Add(key))
            {
                colors.RemoveAt(colorIndex);
                continue;
            }

            var displayName = GetDisplayName(color.Name);
            var normalized = NormalizeName(displayName);
            if (!string.IsNullOrEmpty(normalized) && names.Contains(normalized))
            {
                var replacement = FindUnusedHumanReadableName(color.MainColor, unavailableNames);
                color.Name = CustomStringName.CreateAndRegister(replacement);
                names.Add(NormalizeName(replacement));
                unavailableNames.Add(NormalizeName(replacement));
            }
            else if (!string.IsNullOrEmpty(normalized))
            {
                names.Add(normalized);
            }
            colorIndex++;
        }
    }

    private static HashSet<string> CreateOccupiedNameSet(IEnumerable<CustomColor> registered)
    {
        var names = CreateReservedNameSet();
        names.UnionWith(Palette.ColorNames.Select(GetDisplayName).Select(NormalizeName));
        names.UnionWith(registered.Select(color => NormalizeName(GetDisplayName(color.Name))));
        return names;
    }

    private static HashSet<string> CreateReservedNameSet()
    {
        return PrideColorNames
            .Concat(AnimatedPaletteNames)
            .Concat(MiraOwnedColorNames)
            .Concat(HueCycleDefinitions.Select(definition => definition.Name))
            .Select(NormalizeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static CustomColor? FindByDisplayName(IEnumerable<CustomColor> colors, string name)
    {
        foreach (var color in colors)
        {
            if (NormalizeName(GetDisplayName(color.Name)) == NormalizeName(name))
            {
                return color;
            }
        }
        return null;
    }

    private static Color32 FindColorByDisplayName(
        IEnumerable<CustomColor> registered,
        string name)
    {
        var normalizedName = NormalizeName(name);
        for (var colorId = 0;
             colorId < Palette.ColorNames.Length &&
             colorId < Palette.PlayerColors.Length;
             colorId++)
        {
            if (MatchesPaletteColorName(Palette.ColorNames[colorId], normalizedName))
            {
                return Palette.PlayerColors[colorId];
            }
        }

        var customColor = FindByDisplayName(registered, name);
        if (customColor != null)
        {
            return customColor.MainColor;
        }

        throw new InvalidOperationException(
            $"The required TOU:M phase color '{name}' is not registered.");
    }

    private static Color32 FindFinalizedColorByDisplayName(string name)
    {
        var normalizedName = NormalizeName(name);
        for (var colorId = 0;
             colorId < Palette.ColorNames.Length &&
             colorId < Palette.PlayerColors.Length;
             colorId++)
        {
            if (MatchesPaletteColorName(Palette.ColorNames[colorId], normalizedName))
            {
                return Palette.PlayerColors[colorId];
            }
        }

        foreach (var definition in _orderedDefinitions)
        {
            if (NormalizeName(definition.Name) == normalizedName)
            {
                return definition.Main;
            }
        }

        throw new InvalidOperationException(
            $"The required finalized phase color '{name}' is not registered.");
    }

    private static bool MatchesPaletteColorName(
        StringNames colorName,
        string normalizedName)
    {
        if (NormalizeName(GetDisplayName(colorName)) == normalizedName)
        {
            return true;
        }

        // Early startup has no translation controller yet. Vanilla enum names
        // such as ColorRose and ColorBanana are still good enough for matching.
        var enumName = colorName.ToString();
        if (enumName.StartsWith("Color", StringComparison.OrdinalIgnoreCase))
        {
            enumName = enumName["Color".Length..];
        }
        return NormalizeName(enumName) == normalizedName;
    }

    private static string GetDisplayName(StringNames name)
    {
        if (ReactorHardCodedStringsField.GetValue(null) is Dictionary<StringNames, string> strings &&
            strings.TryGetValue(name, out var hardCoded))
        {
            return hardCoded;
        }

        try
        {
            if (TranslationController.InstanceExists)
            {
                return TranslationController.Instance.GetString(name, Array.Empty<Il2CppSystem.Object>());
            }
        }
        catch
        {
            // Falling back to the enum name is expected during early startup.
        }
        return ((int)name).ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static Color32[] Frames(params string[] hex) => hex.Select(Hex).ToArray();

    private static Color32 Hex(string value)
    {
        if (!ColorUtility.TryParseHtmlString(value, out var color))
        {
            throw new FormatException($"'{value}' is not a valid HTML color.");
        }

        return color;
    }

    private static Color32 CreateShadow(Color32 color) =>
        new((byte)(color.r * 0.58f), (byte)(color.g * 0.58f), (byte)(color.b * 0.58f), byte.MaxValue);

    private static float CalculateLuminance(Color32 color) =>
        (0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b) / 255f;

    private static (double L, double A, double B) ToLab(Color32 color)
    {
        static double Linear(double component)
        {
            component /= 255d;
            return component <= 0.04045d ? component / 12.92d : Math.Pow((component + 0.055d) / 1.055d, 2.4d);
        }

        var r = Linear(color.r);
        var g = Linear(color.g);
        var b = Linear(color.b);
        var x = (r * 0.4124d + g * 0.3576d + b * 0.1805d) / 0.95047d;
        var y = r * 0.2126d + g * 0.7152d + b * 0.0722d;
        var z = (r * 0.0193d + g * 0.1192d + b * 0.9505d) / 1.08883d;
        static double Pivot(double value) =>
            value > 0.008856d ? Math.Pow(value, 1d / 3d) : 7.787d * value + 16d / 116d;
        var fx = Pivot(x);
        var fy = Pivot(y);
        var fz = Pivot(z);
        return (116d * fy - 16d, 500d * (fx - fy), 200d * (fy - fz));
    }

    private static double Distance((double L, double A, double B) left, (double L, double A, double B) right) =>
        Math.Sqrt(Math.Pow(left.L - right.L, 2d) + Math.Pow(left.A - right.A, 2d) +
                  Math.Pow(left.B - right.B, 2d));

    private static double DistanceSquared(
        (double L, double A, double B) left,
        (double L, double A, double B) right)
    {
        var l = left.L - right.L;
        var a = left.A - right.A;
        var b = left.B - right.B;
        return l * l + a * a + b * b;
    }

    private readonly record struct ColorNameCandidate(
        ColorSelectorCategory Category,
        string Name,
        string NormalizedName,
        (double L, double A, double B) Lab);

    private readonly record struct HueCycleDefinition(
        ColorSelectorCategory Category,
        string Name,
        string BaseColorName,
        string[] PhaseNames);

    private readonly record struct SemanticNameCandidate(
        ColorSelectorCategory Category,
        string Name,
        string NormalizedName,
        (double L, double A, double B) Lab);

    private sealed class LabSpatialIndex(double cellSize)
    {
        private readonly Dictionary<
            (int L, int A, int B),
            List<(double L, double A, double B)>> _cells = [];

        public void Add((double L, double A, double B) color)
        {
            var key = GetKey(color);
            if (!_cells.TryGetValue(key, out var bucket))
            {
                bucket = [];
                _cells[key] = bucket;
            }
            bucket.Add(color);
        }

        public bool IsWithinDistance(
            (double L, double A, double B) color,
            double minimumDistance)
        {
            var center = GetKey(color);
            var radius = Math.Max(1, (int)Math.Ceiling(minimumDistance / cellSize));
            var minimumDistanceSquared = minimumDistance * minimumDistance;
            for (var l = center.L - radius; l <= center.L + radius; l++)
            {
                for (var a = center.A - radius; a <= center.A + radius; a++)
                {
                    for (var b = center.B - radius; b <= center.B + radius; b++)
                    {
                        if (!_cells.TryGetValue((l, a, b), out var bucket))
                        {
                            continue;
                        }
                        foreach (var other in bucket)
                        {
                            var deltaL = color.L - other.L;
                            var deltaA = color.A - other.A;
                            var deltaB = color.B - other.B;
                            if (deltaL * deltaL + deltaA * deltaA + deltaB * deltaB <
                                minimumDistanceSquared)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private (int L, int A, int B) GetKey(
            (double L, double A, double B) color) =>
            (
                (int)Math.Floor(color.L / cellSize),
                (int)Math.Floor(color.A / cellSize),
                (int)Math.Floor(color.B / cellSize));
    }

    private sealed class Color32Comparer : IEqualityComparer<Color32>
    {
        public bool Equals(Color32 x, Color32 y) => x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;

        public int GetHashCode(Color32 obj) => HashCode.Combine(obj.r, obj.g, obj.b, obj.a);
    }
}

[HarmonyPatch]
public static class ColorCatalogRegistrationPatch
{
    private static readonly FieldInfo CustomColorsField =
        AccessTools.Field(typeof(PaletteManager), "CustomColors");

    [HarmonyTargetMethod]
    public static MethodBase TargetMethod() => AccessTools.Method(typeof(PaletteManager), "RegisterAllColors");

    [HarmonyPrefix]
    public static void Prefix()
    {
        if (CustomColorsField.GetValue(null) is List<CustomColor> colors)
        {
            ColorCatalog.Prepare(colors);
        }
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        if (CustomColorsField.GetValue(null) is List<CustomColor> colors)
        {
            ColorCatalog.FinalizeCatalog(colors);
        }
    }
}
