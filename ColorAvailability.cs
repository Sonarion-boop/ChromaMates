using AmongUs.Data;
using HarmonyLib;
using InnerNet;
using MiraAPI.GameOptions;
using ChromaMates.Colors;

namespace ChromaMates;

public static class ColorAvailability
{
    private const int VanillaColorCount = 18;
    public const int MiraColorCount = 52;
    public const int DefaultVisibleColorCount = 252;
    private static int _hostSynchronizedColorLimit = MiraColorCount;
    private static int? _hostCompatibilityLimit;
    private static int _lastPreferredRequestGameId = int.MinValue;
    private static int _lastPreferredRequestGeneration = -1;
    private static int _lastRequestedPreferredColorId = -1;
    private static readonly Dictionary<(int Limit, int CatalogSize), int[]> AllowedIdsByLimit = [];

    public static bool IsInNetworkLobby
    {
        get
        {
            try
            {
                return AmongUsClient.Instance && GameData.Instance;
            }
            catch
            {
                return false;
            }
        }
    }

    public static int EffectiveLimit
    {
        get
        {
            if (!IsInNetworkLobby)
            {
                return ColorCatalog.TargetColorCount;
            }

            try
            {
                if (!AmongUsClient.Instance.AmHost)
                {
                    return Math.Clamp(
                        _hostSynchronizedColorLimit,
                        VanillaColorCount,
                        ColorCatalog.TargetColorCount);
                }

                var configured = GetHostConfiguredLimit();
                return _hostCompatibilityLimit.HasValue
                    ? Math.Min(configured, _hostCompatibilityLimit.Value)
                    : configured;
            }
            catch
            {
                // Lobby objects come online over several frames. Keep the last host limit
                // until the next handshake instead of briefly exposing the full catalog.
            }

            return Math.Clamp(
                _hostSynchronizedColorLimit,
                VanillaColorCount,
                ColorCatalog.TargetColorCount);
        }
    }

    internal static int SynchronizedLimit => _hostSynchronizedColorLimit;

    public static void SetSyncedLimit(int limit)
    {
        var synchronized =
            Math.Clamp(limit, VanillaColorCount, ColorCatalog.TargetColorCount);
        if (_hostSynchronizedColorLimit == synchronized)
        {
            return;
        }

        _hostSynchronizedColorLimit = synchronized;
        ColorSelectorTabs.RefreshForNetworkChange();
    }

    internal static int GetHostConfiguredLimit()
    {
        try
        {
            return Math.Clamp(
                GetPresetLimit((ColorCapacityPreset)OptionGroupSingleton<
                    ChromaMatesOptions>.Instance.AvailableColorPreset.Value),
                VanillaColorCount,
                ColorCatalog.TargetColorCount);
        }
        catch
        {
            return DefaultVisibleColorCount;
        }
    }

    internal static void SetHostCompatibilityLimit(int? limit)
    {
        int? normalized = limit.HasValue
            ? Math.Clamp(limit.Value, VanillaColorCount, ColorCatalog.TargetColorCount)
            : null;
        if (_hostCompatibilityLimit == normalized)
        {
            return;
        }

        _hostCompatibilityLimit = normalized;
        ColorSelectorTabs.RefreshForNetworkChange();
    }

    public static bool IsAllowed(int colorId)
    {
        if (colorId < 0 ||
            colorId >= Palette.PlayerColors.Length ||
            ColorCatalog.IsReservedColorId(colorId))
        {
            return false;
        }

        var selectableIndex = colorId < ColorCatalog.FirstReservedColorId
            ? colorId
            : colorId - (ColorCatalog.LastReservedColorId -
                         ColorCatalog.FirstReservedColorId + 1);
        return selectableIndex < EffectiveLimit;
    }

    internal static bool IsRenderableCatalogColor(int colorId)
    {
        if (colorId < 0 ||
            colorId >= Palette.PlayerColors.Length ||
            ColorCatalog.IsReservedColorId(colorId))
        {
            return false;
        }

        return colorId <= ColorCatalog.LastSelectableColorId ||
               ColorCatalog.IsFortegreenFallbackColorId(colorId);
    }

    public static IReadOnlyList<int> GetAllowedIds()
    {
        var catalogSize = Palette.PlayerColors.Length;
        var limit = Math.Min(EffectiveLimit, ColorCatalog.TargetColorCount);
        var key = (limit, catalogSize);
        if (!AllowedIdsByLimit.TryGetValue(key, out var allowedIds))
        {
            allowedIds = ColorCatalog.GetSelectableColorIds(limit).ToArray();
            AllowedIdsByLimit[key] = allowedIds;
        }

        return allowedIds;
    }

    internal static int GetPreferredColorId()
    {
        var preferred = ChromaMatesPlugin.PreferredColorId?.Value ?? -1;
        if (IsSelectableCatalogColor(preferred))
        {
            return preferred;
        }

        try
        {
            return DataManager.Player.Customization.Color;
        }
        catch
        {
            return 0;
        }
    }

    internal static void RememberPreferredColor(int colorId)
    {
        if (!IsSelectableCatalogColor(colorId))
        {
            return;
        }

        if (ChromaMatesPlugin.PreferredColorId != null)
        {
            ChromaMatesPlugin.PreferredColorId.Value = colorId;
        }
    }

    internal static void PrepareVanillaProxyForJoin()
    {
        var colorId = GetPreferredColorId();
        try
        {
            var storedColorId = colorId <= byte.MaxValue
                ? colorId
                : ColorCatalog.FindNearestColorId(
                    colorId,
                    ColorCatalog.GetSelectableColorIds(MiraColorCount)) ?? 0;
            DataManager.Player.Customization.Color = (byte)storedColorId;
        }
        catch
        {
            // Account customization is not ready during the first few startup frames.
        }
    }

    internal static void RequestRememberedColorFromHost(int generation)
    {
        if (!IsInNetworkLobby ||
            !AmongUsClient.Instance ||
            AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer?.Data is not { Disconnected: false })
        {
            return;
        }

        var preferred = GetPreferredColorId();
        var gameId = AmongUsClient.Instance.GameId;
        if (_lastPreferredRequestGameId == gameId &&
            _lastPreferredRequestGeneration == generation &&
            _lastRequestedPreferredColorId == preferred)
        {
            return;
        }

        _lastPreferredRequestGameId = gameId;
        _lastPreferredRequestGeneration = generation;
        _lastRequestedPreferredColorId = preferred;
        ColorNetwork.RpcRequestExtendedColor(PlayerControl.LocalPlayer, preferred);
    }

    internal static void ApplyRememberedHostColor()
    {
        if (!IsInNetworkLobby ||
            !AmongUsClient.Instance ||
            !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer?.Data is not { Disconnected: false })
        {
            return;
        }

        var preferred = GetPreferredColorId();
        var gameId = AmongUsClient.Instance.GameId;
        if (_lastPreferredRequestGameId == gameId &&
            _lastRequestedPreferredColorId == preferred)
        {
            return;
        }

        _lastPreferredRequestGameId = gameId;
        _lastPreferredRequestGeneration = -1;
        _lastRequestedPreferredColorId = preferred;
        ColorNetwork.ApplyRequestedColorAsHost(PlayerControl.LocalPlayer, preferred);
    }

    internal static void ResetPreferredColorRequest()
    {
        _lastPreferredRequestGameId = int.MinValue;
        _lastPreferredRequestGeneration = -1;
        _lastRequestedPreferredColorId = -1;
    }

    internal static int? FindNearestAllowedColorId(
        int sourceColorId,
        IReadOnlySet<int>? occupied = null)
    {
        var candidates = GetAllowedIds()
            .Where(colorId => occupied == null || !occupied.Contains(colorId));
        return ColorCatalog.FindNearestColorId(sourceColorId, candidates);
    }

    internal static bool AssignNearestAllowedColor(
        PlayerControl player,
        int sourceColorId,
        IReadOnlySet<int>? occupied = null)
    {
        var replacement = FindNearestAllowedColorId(sourceColorId, occupied);
        if (!replacement.HasValue)
        {
            return false;
        }

        ApplyHostColor(player, replacement.Value);
        return true;
    }

    public static void EnforceRoster(bool useFortegreenForInvalid = true)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost)
        {
            return;
        }

        var allowedColorIds = GetAllowedIds();
        if (allowedColorIds.Count == 0)
        {
            return;
        }

        var occupied = new HashSet<int>();
        foreach (var player in PlayerControl.AllPlayerControls.ToArray()
                     .Where(player => player?.Data is { Disconnected: false }))
        {
            var current = player.Data.DefaultOutfit.ColorId;
            if (!IsAllowed(current))
            {
                if (useFortegreenForInvalid)
                {
                    ApplyFortegreenFallback(player);
                    continue;
                }
            }
            else if (occupied.Add(current))
            {
                continue;
            }

            var replacement = FindNearestAllowedColorId(current, occupied);
            if (!replacement.HasValue)
            {
                Reactor.Utilities.Logger<ChromaMatesPlugin>.Warning(
                    "There are not enough selectable colors to give every active player " +
                    "a unique color.");
                continue;
            }

            occupied.Add(replacement.Value);
            ApplyHostColor(player, replacement.Value);
        }
    }

    internal static void ApplyHostColor(PlayerControl player, int colorId)
    {
        if (player?.Data is not { Disconnected: false } ||
            PlayerControl.LocalPlayer == null)
        {
            return;
        }

        if (colorId < ColorCatalog.FirstReservedColorId)
        {
            player.RpcSetColor((byte)colorId);
            return;
        }

        ColorNetwork.RpcApplyExtendedColor(
            PlayerControl.LocalPlayer,
            player,
            colorId);
    }

    internal static void ApplyFortegreenFallback(PlayerControl player)
    {
        if (player?.Data is not { Disconnected: false } ||
            player.Data.DefaultOutfit.ColorId == ColorCatalog.FortegreenFallbackColorId ||
            PlayerControl.LocalPlayer == null)
        {
            return;
        }

        ColorNetwork.RpcApplyExtendedColor(
            PlayerControl.LocalPlayer,
            player,
            ColorCatalog.FortegreenFallbackColorId);
    }

    private static bool IsSelectableCatalogColor(int colorId) =>
        colorId >= 0 &&
        colorId < Palette.PlayerColors.Length &&
        !ColorCatalog.IsReservedColorId(colorId) &&
        !ColorCatalog.IsFortegreenFallbackColorId(colorId);

    private static int GetPresetLimit(ColorCapacityPreset preset)
    {
        return preset switch
        {
            ColorCapacityPreset.Mira => MiraColorCount,
            ColorCapacityPreset.OneHundred => 100,
            ColorCapacityPreset.TwoHundredFiftyTwo => DefaultVisibleColorCount,
            ColorCapacityPreset.FiveHundred => 500,
            ColorCapacityPreset.OneThousand => 1_000,
            ColorCapacityPreset.FifteenHundred => 1_500,
            ColorCapacityPreset.TwoThousand => 2_000,
            ColorCapacityPreset.TwentyFiveHundred => 2_500,
            _ => ColorCatalog.TargetColorCount
        };
    }
}

[HarmonyPatch(typeof(PlayerTab), nameof(PlayerTab.SelectColor))]
public static class RestrictedColorSelectionPatch
{
    [HarmonyPrefix]
    public static bool Prefix([HarmonyArgument(0)] int colorId) => ColorAvailability.IsAllowed(colorId);

    [HarmonyPostfix]
    public static void Postfix(
        PlayerTab __instance,
        [HarmonyArgument(0)] int colorId)
    {
        if (ColorAvailability.IsAllowed(colorId))
        {
            ColorSelectorTabs.ApplyHighlightedSelection(__instance, colorId);
        }
    }
}

[HarmonyPatch(typeof(PlayerTab), nameof(PlayerTab.ClickEquip))]
public static class ExtendedColorEquipPatch
{
    [HarmonyPrefix]
    public static bool Prefix(PlayerTab __instance)
    {
        var colorId = __instance.currentColor;
        if (!ColorAvailability.IsAllowed(colorId))
        {
            return false;
        }

        var previousColorId = ColorAvailability.GetPreferredColorId();
        ColorAvailability.RememberPreferredColor(colorId);
        if (!ColorAvailability.IsInNetworkLobby)
        {
            ColorSelectorTabs.ApplyEquippedSelection(
                colorId,
                previousColorId,
                refreshNetworkAvailability: false);
            return false;
        }

        if (colorId < ColorCatalog.FirstReservedColorId)
        {
            return true;
        }

        if (!PlayerControl.LocalPlayer)
        {
            return false;
        }

        ColorNetwork.RpcRequestExtendedColor(PlayerControl.LocalPlayer, colorId);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckColor))]
public static class RestrictedColorRequestPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] byte bodyColor)
    {
        if (!AmongUsClient.Instance.AmHost || ColorAvailability.IsAllowed(bodyColor))
        {
            return true;
        }

        if (!ColorAvailability.AssignNearestAllowedColor(__instance, bodyColor))
        {
            Reactor.Utilities.Logger<ChromaMatesPlugin>.Warning(
                $"No permitted replacement color was available for player " +
                $"{__instance.PlayerId}.");
        }
        return false;
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.JoinGame))]
public static class PreferredColorJoinPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        ColorAvailability.PrepareVanillaProxyForJoin();
    }
}
