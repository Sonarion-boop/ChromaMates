using AmongUs.Data;
using HarmonyLib;
using MiraAPI.Utilities.Assets;
using ChromaMates.Colors;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ChromaMates;

public enum ColorSelectorCategory
{
    Reds,
    Oranges,
    Yellows,
    Greens,
    Cyans,
    Blues,
    Purples,
    Pinks,
    Neutrals,
    Palettes,
    Prides
}

[HarmonyPatch(typeof(PlayerTab))]
public static class ColorSelectorTabs
{
    // These offsets are relative to PlayerTab, not the full-screen wardrobe.
    private const int VisibleRows = 4;
    private const float GridHorizontalOffset = 2.15f;
    private const float GridVerticalOffset = 2.48f;
    private static readonly ColorSelectorCategory[] SelectableCategories =
        Enum.GetValues<ColorSelectorCategory>();

    private static PlayerTab? _activePlayerTab;
    private static TextMeshPro? _categoryTitle;
    private static int _selectedCategoryIndex;
    private static int _highlightedColorId = -1;
    private static PoolablePlayer[] _wardrobePreviews = [];
    private static int[] _visibleAnimatedColorIds = [];
    private static readonly Dictionary<
        (int CatalogSize, int Limit, ColorSelectorCategory Category),
        int[]> VisibleIdsByView = [];

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(PlayerTab.OnEnable))]
    public static void OnEnablePrefix(out PaletteSnapshot? __state)
    {
        __state = null;
        var fullCount = Palette.PlayerColors.Length;
        var currentColor = PlayerControl.LocalPlayer?.Data?.DefaultOutfit.ColorId ??
                           DataManager.Player.Customization.Color;
        var allowedColorIds = ColorAvailability.GetAllowedIds();
        var largestAllowedColorId = allowedColorIds.Count == 0
            ? 17
            : allowedColorIds[^1];
        var chipCount = Math.Clamp(
            Math.Max(
                largestAllowedColorId + 1,
                currentColor + 1),
            18,
            fullCount);
        if (chipCount >= fullCount)
        {
            return;
        }

        __state = new PaletteSnapshot(
            Palette.ColorNames.ToArray(),
            Palette.PlayerColors.ToArray(),
            Palette.ShadowColors.ToArray(),
            Palette.TextColors.ToArray(),
            Palette.TextOutlineColors.ToArray());
        Palette.ColorNames = __state.ColorNames.Take(chipCount).ToArray();
        Palette.PlayerColors = __state.PlayerColors.Take(chipCount).ToArray();
        Palette.ShadowColors = __state.ShadowColors.Take(chipCount).ToArray();
        Palette.TextColors = __state.TextColors.Take(chipCount).ToArray();
        Palette.TextOutlineColors = __state.TextOutlineColors.Take(chipCount).ToArray();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerTab.OnEnable))]
    public static void OnEnablePostfix(
        PlayerTab __instance,
        PaletteSnapshot? __state)
    {
        __state?.Restore();
        _activePlayerTab = __instance;
        _highlightedColorId = __instance.currentColor;
        CaptureWardrobePreviews(__instance);
        EnsureControls(__instance);
        RefreshLayout();
        RestoreOfflinePreferredColor(__instance);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Color menu opened: {ColorAvailability.EffectiveLimit} available, " +
            $"host limit {ColorAvailability.SynchronizedLimit}, " +
            $"in lobby {ColorAvailability.IsInNetworkLobby}, " +
            $"{__instance.ColorChips.Count} chips built.");
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(PlayerTab.OnEnable))]
    public static Exception? OnEnableFinalizer(
        Exception? __exception,
        PaletteSnapshot? __state)
    {
        __state?.Restore();
        return __exception;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerTab.Update))]
    public static void UpdatePostfix(PlayerTab __instance)
    {
        if (!__instance.gameObject.activeInHierarchy)
        {
            return;
        }

        _activePlayerTab = __instance;
        RefreshAnimatedChips(__instance);
        RefreshHighlightedPreview(__instance);
    }

    private static void RestoreOfflinePreferredColor(PlayerTab tab)
    {
        if (ColorAvailability.IsInNetworkLobby)
        {
            return;
        }

        var preferredColorId = ColorAvailability.GetPreferredColorId();
        if (!ColorAvailability.IsAllowed(preferredColorId))
        {
            return;
        }

        var previousColorId = DataManager.Player.Customization.Color;
        tab.SelectColor(preferredColorId);
        ApplyEquippedSelection(
            preferredColorId,
            previousColorId,
            refreshNetworkAvailability: false);
    }

    private static void EnsureControls(PlayerTab tab)
    {
        var next = tab.transform.Find("ChromaMatesColorNext")?.GetComponent<PassiveButton>();
        if (!next)
        {
            next = CreateArrow(tab, "ChromaMatesColorNext", new Vector3(2.91f, tab.YStart + 0.74f, -55f), false);
            next.OnClick.AddListener((UnityAction)(() => ChangeCategory(1)));
        }

        var previous = tab.transform.Find("ChromaMatesColorPrevious")?.GetComponent<PassiveButton>();
        if (!previous)
        {
            previous = CreateArrow(tab, "ChromaMatesColorPrevious", new Vector3(-1.19f, tab.YStart + 0.74f, -55f), true);
            previous.OnClick.AddListener((UnityAction)(() => ChangeCategory(-1)));
        }

        _categoryTitle = tab.transform.Find("Text")?.GetComponent<TextMeshPro>();
        var title = _categoryTitle;
        if (title != null && title)
        {
            var translator = title.GetComponent<TextTranslatorTMP>();
            if (translator)
            {
                UnityEngine.Object.Destroy(translator);
            }

            title.alignment = TextAlignmentOptions.Center;
            title.transform.localPosition = new Vector3(0.86f, tab.YStart + 0.74f, -55f);
            title.fontSize = title.fontSizeMax = 4.6f;
            title.rectTransform.sizeDelta = new Vector2(3.5f, 1f);
        }

    }

    private static PassiveButton CreateArrow(PlayerTab tab, string name, Vector3 position, bool flip)
    {
        var arrow = UnityEngine.Object.Instantiate(
            PlayerCustomizationMenu.Instance.BackButton,
            tab.transform).GetComponent<PassiveButton>();
        arrow.name = name;
        arrow.transform.localPosition = position;
        arrow.transform.localScale = new Vector3(0.5f, 0.5f, 1f);

        var aspect = arrow.GetComponent<AspectPosition>();
        if (aspect)
        {
            UnityEngine.Object.Destroy(aspect);
        }

        var close = arrow.GetComponent<CloseButtonConsoleBehaviour>();
        if (close)
        {
            UnityEngine.Object.Destroy(close);
        }

        arrow.OnClick = new Button.ButtonClickedEvent();
        var renderer = arrow.GetComponent<SpriteRenderer>();
        renderer.sprite = MiraAssets.NextButton.LoadAsset();
        renderer.flipX = flip;
        return arrow;
    }

    private static void ChangeCategory(int direction)
    {
        _selectedCategoryIndex =
            (_selectedCategoryIndex + direction + SelectableCategories.Length) %
            SelectableCategories.Length;
        RefreshLayout();
    }

    internal static void RefreshForNetworkChange()
    {
        if (_activePlayerTab != null && _activePlayerTab)
        {
            RefreshLayout();
        }
    }

    internal static void ApplyEquippedSelection(
        int colorId,
        int previousColorId,
        bool refreshNetworkAvailability)
    {
        var tab = _activePlayerTab;
        if (tab == null || !tab)
        {
            return;
        }

        if (refreshNetworkAvailability)
        {
            tab.UpdateAvailableColors();
        }

        tab.currentColor = colorId;
        tab.currentColorIsEquipped = true;
        _highlightedColorId = colorId;
        ReleaseOfflineColorChip(tab, previousColorId);
        MarkOfflineColorChipEquipped(tab, colorId);
        RefreshHighlightedPreview(tab);
    }

    internal static void ApplyHighlightedSelection(PlayerTab tab, int colorId)
    {
        if (!ColorAvailability.IsRenderableCatalogColor(colorId))
        {
            return;
        }

        _activePlayerTab = tab;
        _highlightedColorId = colorId;
        tab.currentColor = colorId;
        CaptureWardrobePreviews(tab);
        var refreshed = ApplyWardrobePreviewColor(colorId, force: true);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Wardrobe selected color {colorId}; repainted {refreshed} " +
            "active preview object(s).");
    }

    internal static void RefreshHighlightedPreview()
    {
        var tab = _activePlayerTab;
        if (tab != null && tab)
        {
            RefreshHighlightedPreview(tab);
        }
    }

    private static void RefreshHighlightedPreview(PlayerTab tab)
    {
        if (!tab.gameObject.activeInHierarchy ||
            !ColorAvailability.IsAllowed(_highlightedColorId) ||
            !ColorAvailability.IsRenderableCatalogColor(_highlightedColorId))
        {
            return;
        }

        tab.currentColor = _highlightedColorId;
        if (_wardrobePreviews.Length == 0 ||
            _wardrobePreviews.All(preview => !preview))
        {
            CaptureWardrobePreviews(tab);
        }
        ApplyWardrobePreviewColor(_highlightedColorId, force: false);
    }

    private static int ApplyWardrobePreviewColor(int colorId, bool force)
    {
        var refreshed = 0;
        foreach (var preview in _wardrobePreviews)
        {
            if (!preview || !preview.gameObject.activeInHierarchy)
            {
                continue;
            }

            ApplyPreviewColor(preview, colorId, force);
            refreshed++;
        }

        return refreshed;
    }

    private static void ApplyPreviewColor(
        PoolablePlayer preview,
        int colorId,
        bool force)
    {
        if (preview && (force || preview.ColorId != colorId))
        {
            preview.SetBodyColor(colorId);
        }

        var renderers = new List<Renderer>();
        AddRenderer(renderers, preview.Cosmetics?.currentBodySprite?.BodySprite);
        var longModeParts = preview.Cosmetics?.currentBodySprite?.LongModeParts;
        if (longModeParts != null)
        {
            foreach (var renderer in longModeParts)
            {
                AddRenderer(renderers, renderer);
            }
        }
        foreach (var renderer in preview.Hands)
        {
            AddRenderer(renderers, renderer);
        }
        foreach (var renderer in preview.OtherBodySprites)
        {
            AddRenderer(renderers, renderer);
        }

        foreach (var renderer in renderers)
        {
            var behaviour =
                renderer.gameObject.GetComponent<WardrobePreviewColorBehaviour>() ??
                renderer.gameObject.AddComponent<WardrobePreviewColorBehaviour>();
            behaviour.Configure(renderer, colorId);
        }
    }

    private static void CaptureWardrobePreviews(PlayerTab tab)
    {
        var previews = new List<PoolablePlayer>();
        AddPreview(previews, tab.PlayerPreview);
        var menu = PlayerCustomizationMenu.Instance;
        if (menu)
        {
            AddPreview(previews, menu.PreviewArea);
            foreach (var preview in menu.GetComponentsInChildren<PoolablePlayer>(true))
            {
                AddPreview(previews, preview);
            }
        }

        _wardrobePreviews = previews.ToArray();
    }

    private static void AddPreview(List<PoolablePlayer> previews, PoolablePlayer preview)
    {
        if (!preview || previews.Any(existing =>
                existing && existing.GetInstanceID() == preview.GetInstanceID()))
        {
            return;
        }

        previews.Add(preview);
    }

    private static void AddRenderer(List<Renderer> renderers, Renderer? renderer)
    {
        if (renderer == null || !renderer || renderers.Any(existing =>
                existing && existing.GetInstanceID() == renderer.GetInstanceID()))
        {
            return;
        }

        renderers.Add(renderer);
    }

    private static void ReleaseOfflineColorChip(PlayerTab tab, int colorId)
    {
        if (colorId < 0 || colorId >= tab.ColorChips.Count || colorId == tab.currentColor)
        {
            return;
        }

        tab.AvailableColors?.Add(colorId);
        var chip = tab.ColorChips[colorId];
        chip.Inner.SetMaterialColor(colorId);
        chip.PlayerEquippedForeground.SetActive(false);
        chip.InUseForeground.SetActive(false);
        chip.Button.enabled = true;
    }

    private static void MarkOfflineColorChipEquipped(PlayerTab tab, int colorId)
    {
        if (colorId < 0 || colorId >= tab.ColorChips.Count)
        {
            return;
        }

        tab.AvailableColors?.Remove(colorId);
        var chip = tab.ColorChips[colorId];
        chip.PlayerEquippedForeground.SetActive(true);
        chip.InUseForeground.SetActive(false);
    }

    private static void RefreshLayout()
    {
        var tab = _activePlayerTab;
        if (tab == null || !tab || tab.ColorChips == null)
        {
            return;
        }

        var visibleColorIds = GetVisibleColorIds();
        _visibleAnimatedColorIds = visibleColorIds
            .Where(AnimatedColorRenderer.IsAnimated)
            .ToArray();
        EnsureDirectChipCapacity(
            tab,
            visibleColorIds.Length == 0 ? 0 : visibleColorIds.Max() + 1);

        for (var chipIndex = 0; chipIndex < tab.ColorChips.Count; chipIndex++)
        {
            tab.ColorChips[chipIndex].gameObject.SetActive(false);
        }

        for (var visibleIndex = 0; visibleIndex < visibleColorIds.Length; visibleIndex++)
        {
            var colorId = visibleColorIds[visibleIndex];
            if (colorId < 0 || colorId >= tab.ColorChips.Count)
            {
                continue;
            }

            var chip = tab.ColorChips[colorId];
            chip.gameObject.SetActive(true);

            var horizontalStepCount = Math.Max(1, tab.NumPerRow - 1);
            var column = visibleIndex % tab.NumPerRow;
            var row = visibleIndex / tab.NumPerRow;
            var x = tab.XRange.Lerp(column / (float)horizontalStepCount) + GridHorizontalOffset;
            var y = tab.YStart - GridVerticalOffset - row * tab.YOffset;
            chip.transform.localPosition = new Vector3(x, y, -1f);
        }

        ConfigureScroller(tab, visibleColorIds.Length);
        var title = _categoryTitle;
        if (title != null && title)
        {
            var selectedCategory = SelectableCategories[_selectedCategoryIndex];
            title.text = $"{selectedCategory.ToString().ToUpperInvariant()} " +
                         $"{visibleColorIds.Length}/{ColorAvailability.EffectiveLimit}";
        }
    }

    private static void EnsureDirectChipCapacity(PlayerTab tab, int required)
    {
        if (tab.ColorChips.Count == 0 || required <= tab.ColorChips.Count)
        {
            return;
        }

        var template = tab.ColorChips[0];
        var parent = tab.scroller ? tab.scroller.Inner : tab.ColorTabArea;
        while (tab.ColorChips.Count < required)
        {
            var colorId = tab.ColorChips.Count;
            var chip = UnityEngine.Object.Instantiate(tab.ColorTabPrefab, parent);
            chip.name = $"ChromaMatesColorChip{colorId}";
            chip.Button.ClickMask = template.Button.ClickMask;
            var renderer = chip.GetComponent<SpriteRenderer>();
            if (renderer)
            {
                renderer.maskInteraction =
                    template.GetComponent<SpriteRenderer>().maskInteraction;
            }
            chip.Inner.SetMaterialColor(colorId);
            chip.Button.OnClick = new Button.ButtonClickedEvent();
            chip.Button.OnClick.AddListener((UnityAction)(() =>
                tab.SelectColor(colorId)));
            tab.ColorChips.Add(chip);
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Color selector expanded directly to {tab.ColorChips.Count} chips " +
            $"for {ColorAvailability.EffectiveLimit} selectable lobby colors.");
    }

    private static int[] GetVisibleColorIds()
    {
        var catalogSize = Palette.PlayerColors.Length;
        var limit = Math.Min(ColorAvailability.EffectiveLimit, catalogSize);
        var selectedCategory = SelectableCategories[_selectedCategoryIndex];
        var cacheKey = (catalogSize, limit, selectedCategory);
        if (VisibleIdsByView.TryGetValue(cacheKey, out var visibleColorIds))
        {
            return visibleColorIds;
        }

        visibleColorIds = ColorCatalog.GetDefinitions()
            .Where(definition => ColorAvailability.IsAllowed(definition.Id) &&
                                 BelongsToCategory(definition, selectedCategory))
            .OrderBy(definition => ColorCatalog.IsFamilyCycleName(definition.Name) ? 1 : 0)
            .ThenByDescending(definition => GetLuminanceSortKey(definition, selectedCategory))
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(definition => definition.Id)
            .ToArray();
        VisibleIdsByView[cacheKey] = visibleColorIds;
        return visibleColorIds;
    }

    internal static void InvalidateCatalogViews()
    {
        VisibleIdsByView.Clear();
        _visibleAnimatedColorIds = [];
    }

    private static bool BelongsToCategory(
        ColorCatalogDefinition definition,
        ColorSelectorCategory category)
    {
        if (definition.Categories.HasFlag(ColorCatalogCategory.Pride))
        {
            return category == ColorSelectorCategory.Prides;
        }
        if (definition.Name.Equals("Monochrome", StringComparison.OrdinalIgnoreCase))
        {
            return category == ColorSelectorCategory.Neutrals;
        }
        if (definition.Categories.HasFlag(ColorCatalogCategory.Palettes) ||
            definition.Name.Equals("Rainbow", StringComparison.OrdinalIgnoreCase))
        {
            return category == ColorSelectorCategory.Palettes;
        }
        if (category is ColorSelectorCategory.Prides or ColorSelectorCategory.Palettes)
        {
            return false;
        }

        return category == ClassifyStaticColor(definition.Main, definition.Shadow);
    }

    internal static ColorSelectorCategory ClassifyStaticColor(Color32 main, Color32 shadow)
    {
        Color.RGBToHSV(main, out var hue, out var saturation, out _);
        if (saturation < 0.12f)
        {
            return ColorSelectorCategory.Neutrals;
        }

        var degrees = hue * 360f;
        return degrees switch
        {
            < 15f => ColorSelectorCategory.Reds,
            < 45f => ColorSelectorCategory.Oranges,
            < 70f => ColorSelectorCategory.Yellows,
            < 165f => ColorSelectorCategory.Greens,
            < 195f => ColorSelectorCategory.Cyans,
            < 255f => ColorSelectorCategory.Blues,
            < 295f => ColorSelectorCategory.Purples,
            < 345f => ColorSelectorCategory.Pinks,
            _ => ColorSelectorCategory.Reds
        };
    }

    private static float GetLuminanceSortKey(
        ColorCatalogDefinition definition,
        ColorSelectorCategory category)
    {
        if (category is ColorSelectorCategory.Palettes or ColorSelectorCategory.Prides)
        {
            return 0f;
        }

        return (0.2126f * definition.Main.r + 0.7152f * definition.Main.g +
                0.0722f * definition.Main.b) / 255f;
    }

    private static void ConfigureScroller(PlayerTab tab, int colorCount)
    {
        if (tab.scroller == null)
        {
            return;
        }

        tab.scroller.enabled = true;
        tab.scroller.allowY = true;
        tab.scroller.CalculateAndSetYBounds(
            colorCount,
            Math.Max(1, tab.NumPerRow),
            VisibleRows,
            tab.YOffset);
        tab.scroller.ScrollToTop();
    }

    private static void RefreshAnimatedChips(PlayerTab tab)
    {
        foreach (var colorId in _visibleAnimatedColorIds)
        {
            if (colorId >= 0 &&
                colorId < tab.ColorChips.Count &&
                tab.ColorChips[colorId].gameObject.activeInHierarchy)
            {
                tab.ColorChips[colorId].Inner.SpriteColor =
                    AnimatedColorRenderer.GetColors(colorId).Main;
            }
        }
    }

    public sealed class PaletteSnapshot(
        StringNames[] colorNames,
        Color32[] playerColors,
        Color32[] shadowColors,
        Color32[] textColors,
        Color32[] textOutlineColors)
    {
        private bool _restored;

        public StringNames[] ColorNames { get; } = colorNames;
        public Color32[] PlayerColors { get; } = playerColors;
        public Color32[] ShadowColors { get; } = shadowColors;
        public Color32[] TextColors { get; } = textColors;
        public Color32[] TextOutlineColors { get; } = textOutlineColors;

        public void Restore()
        {
            if (_restored)
            {
                return;
            }

            Palette.ColorNames = ColorNames;
            Palette.PlayerColors = PlayerColors;
            Palette.ShadowColors = ShadowColors;
            Palette.TextColors = TextColors;
            Palette.TextOutlineColors = TextOutlineColors;
            _restored = true;
        }
    }
}
