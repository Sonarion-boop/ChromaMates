using System.Reflection;
using System.Text.RegularExpressions;
using AmongUs.Data;
using HarmonyLib;
using MiraAPI.LocalSettings;
using MiraAPI.Utilities;
using ChromaMates.Colors;
using Reactor.Utilities.Attributes;
using UnityEngine;
using Object = Il2CppSystem.Object;

namespace ChromaMates;

public static class AnimatedColorRenderer
{
    private static readonly Regex RichTextTagPattern = new("<.*?>", RegexOptions.Compiled);
    private static readonly Color FortegreenBodyColor = new Color32(38, 166, 98, 255);
    private static readonly Color FortegreenShadowColor = new Color32(18, 63, 28, 255);
    private static readonly Dictionary<int, RenderedPlayerColors> FrameColors = [];
    private static int _cachedFrame = -1;
    private static float _nextRainbowSettingRefreshTime;
    private static bool _cachedRainbowAsFortegreen;

    public static bool IsAnimated(int colorId) =>
        colorId >= 0 && colorId < Palette.PlayerColors.Length && ColorCatalog.IsAnimated(colorId);

    public static RenderedPlayerColors GetColors(int colorId)
    {
        if (ColorCatalog.IsRainbow(colorId) && RainbowAsFortegreen())
        {
            return new RenderedPlayerColors(FortegreenBodyColor, FortegreenShadowColor);
        }

        var frame = Time.frameCount;
        if (_cachedFrame != frame)
        {
            FrameColors.Clear();
            _cachedFrame = frame;
        }
        if (FrameColors.TryGetValue(colorId, out var colors))
        {
            return colors;
        }

        colors = ColorCatalog.GetRenderedColors(colorId, ColorCatalog.SynchronizedTime);
        FrameColors[colorId] = colors;
        return colors;
    }

    public static void ApplyPlayerColors(Renderer renderer, int colorId)
    {
        var colors = GetColors(colorId);
        renderer.material.SetColor(ShaderID.BackColor, colors.Shadow);
        renderer.material.SetColor(ShaderID.BodyColor, colors.Main);
        renderer.material.SetColor(ShaderID.VisorColor, Palette.VisorColor);
    }

    public static string StripRichText(string value) =>
        RichTextTagPattern.Replace(value, string.Empty);

    private static bool RainbowAsFortegreen()
    {
        if (Time.unscaledTime < _nextRainbowSettingRefreshTime)
        {
            return _cachedRainbowAsFortegreen;
        }

        _nextRainbowSettingRefreshTime = Time.unscaledTime + 0.5f;
        try
        {
            var settingsType = AccessTools.TypeByName("TownOfUs.TownOfUsLocalMiscSettings");
            if (settingsType == null)
            {
                _cachedRainbowAsFortegreen = false;
                return _cachedRainbowAsFortegreen;
            }

            var singletonType = typeof(LocalSettingsTabSingleton<>).MakeGenericType(settingsType);
            var instance = AccessTools.Property(singletonType, "Instance")?.GetValue(null);
            var entry = AccessTools.Property(settingsType, "RainbowColorAsFortegreen")?.GetValue(instance);
            _cachedRainbowAsFortegreen =
                entry != null &&
                AccessTools.Property(entry.GetType(), "Value")?.GetValue(entry) is true;
        }
        catch
        {
            _cachedRainbowAsFortegreen = false;
        }

        return _cachedRainbowAsFortegreen;
    }
}

[RegisterInIl2Cpp]
public sealed class AnimatedPlayerColorBehaviour(IntPtr cppPtr) : MonoBehaviour(cppPtr)
{
    private const float RefreshInterval = 1f / 30f;

    public Renderer TargetRenderer;
    public int ColorId = -1;
    public float NextRefreshTime;

    public void Update()
    {
        if (TargetRenderer == null || Time.unscaledTime < NextRefreshTime)
        {
            return;
        }

        NextRefreshTime = Time.unscaledTime + RefreshInterval;
        AnimatedColorRenderer.ApplyPlayerColors(TargetRenderer, ColorId);
    }

    public void Configure(Renderer renderer, int colorId)
    {
        TargetRenderer = renderer;
        ColorId = colorId;
        NextRefreshTime = 0f;
        enabled = true;
    }

    public void Disable()
    {
        ColorId = -1;
        enabled = false;
    }
}

[RegisterInIl2Cpp]
public sealed class AnimatedBasicColorBehaviour(IntPtr cppPtr) : MonoBehaviour(cppPtr)
{
    private const float RefreshInterval = 1f / 30f;

    public SpriteRenderer TargetRenderer;
    public int ColorId = -1;
    public float NextRefreshTime;

    public void Update()
    {
        if (TargetRenderer == null || Time.unscaledTime < NextRefreshTime)
        {
            return;
        }

        NextRefreshTime = Time.unscaledTime + RefreshInterval;
        TargetRenderer.color = AnimatedColorRenderer.GetColors(ColorId).Main;
    }

    public void Configure(SpriteRenderer renderer, int colorId)
    {
        TargetRenderer = renderer;
        ColorId = colorId;
        NextRefreshTime = 0f;
        enabled = true;
    }

    public void Disable()
    {
        ColorId = -1;
        enabled = false;
    }
}

[RegisterInIl2Cpp]
public sealed class WardrobePreviewColorBehaviour(IntPtr cppPtr) : MonoBehaviour(cppPtr)
{
    public Renderer TargetRenderer;
    public int ColorId = -1;

    public void LateUpdate()
    {
        if (TargetRenderer == null ||
            !TargetRenderer ||
            !TargetRenderer.gameObject.activeInHierarchy ||
            ColorId < 0)
        {
            return;
        }

        AnimatedColorRenderer.ApplyPlayerColors(TargetRenderer, ColorId);
    }

    public void Configure(Renderer renderer, int colorId)
    {
        TargetRenderer = renderer;
        ColorId = colorId;
        enabled = true;
        AnimatedColorRenderer.ApplyPlayerColors(renderer, colorId);
    }
}

[HarmonyPatch(typeof(PlayerMaterial), nameof(PlayerMaterial.SetColors), typeof(int), typeof(Renderer))]
public static class AnimatedPlayerMaterialPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix([HarmonyArgument(0)] int colorId, [HarmonyArgument(1)] Renderer renderer)
    {
        if (!AnimatedColorRenderer.IsAnimated(colorId))
        {
            renderer.gameObject.GetComponent<AnimatedPlayerColorBehaviour>()?.Disable();
            return true;
        }

        var behaviour = renderer.gameObject.GetComponent<AnimatedPlayerColorBehaviour>() ??
                        renderer.gameObject.AddComponent<AnimatedPlayerColorBehaviour>();
        behaviour.Configure(renderer, colorId);
        AnimatedColorRenderer.ApplyPlayerColors(renderer, colorId);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerMaterial), nameof(PlayerMaterial.SetColors), typeof(Color), typeof(Renderer))]
public static class StaticPlayerMaterialResetPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static void Prefix([HarmonyArgument(1)] Renderer renderer)
    {
        var behaviour = renderer.gameObject.GetComponent<AnimatedPlayerColorBehaviour>();
        if (behaviour != null)
        {
            behaviour.Disable();
        }
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
public static class AnimatedPlayerFrameFallbackPatch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerControl __instance)
    {
        if (__instance?.cosmetics?.currentBodySprite?.BodySprite == null)
        {
            return;
        }

        var colorId = __instance.cosmetics.ColorId;
        if (!AnimatedColorRenderer.IsAnimated(colorId))
        {
            return;
        }

        var renderer = __instance.cosmetics.currentBodySprite.BodySprite;
        var behaviour = renderer.gameObject.GetComponent<AnimatedPlayerColorBehaviour>();
        if (behaviour == null)
        {
            behaviour = renderer.gameObject.AddComponent<AnimatedPlayerColorBehaviour>();
            behaviour.Configure(renderer, colorId);
            AnimatedColorRenderer.ApplyPlayerColors(renderer, colorId);
        }
    }
}

[HarmonyPatch(typeof(ChatNotification), nameof(ChatNotification.Update))]
public static class AnimatedChatPortraitPatch
{
    [HarmonyPostfix]
    public static void Postfix(ChatNotification __instance)
    {
        if (!__instance.gameObject.active || !AnimatedColorRenderer.IsAnimated(__instance.player.cosmetics.ColorId))
        {
            return;
        }

        var html = ColorUtility.ToHtmlStringRGB(
            AnimatedColorRenderer.GetColors(__instance.player.cosmetics.ColorId).Main);
        __instance.playerNameText.text =
            $"<color=#{html}>{AnimatedColorRenderer.StripRichText(__instance.playerNameText.text)}</color>";
    }
}

[HarmonyPatch(typeof(HostInfoPanel), nameof(HostInfoPanel.Update))]
public static class AnimatedHostPanelPatch
{
    [HarmonyPostfix]
    public static void Postfix(HostInfoPanel __instance)
    {
        if (__instance == null || !__instance || __instance.gameObject == null)
        {
            return;
        }

        if (!__instance.gameObject.activeInHierarchy)
        {
            return;
        }

        var playerPreview = __instance.player;
        if (playerPreview == null ||
            playerPreview.cosmetics == null ||
            __instance.hostLabel == null ||
            __instance.playerName == null)
        {
            return;
        }

        var colorId = playerPreview.cosmetics.ColorId;
        if (!AnimatedColorRenderer.IsAnimated(colorId))
        {
            return;
        }

        var gameData = GameData.Instance;
        if (gameData == null)
        {
            return;
        }

        var host = gameData.GetHost();
        var translations = TranslationController.Instance;
        var client = AmongUsClient.Instance;
        if (host == null || translations == null || client == null)
        {
            return;
        }

        var html = ColorUtility.ToHtmlStringRGB(
            AnimatedColorRenderer.GetColors(colorId).Main);
        __instance.hostLabel.text =
            translations.GetString(StringNames.HostNounLabel, Array.Empty<Object>());
        if (__instance.ShouldBoldenHostLabel(DataManager.Settings.Language.CurrentLanguage))
        {
            __instance.hostLabel.text = $"<b>{__instance.hostLabel.text}</b>";
        }

        var hostName = string.IsNullOrEmpty(host.PlayerName) ? "..." : $"<color=#{html}>{host.PlayerName}</color>";
        __instance.playerName.text = client.AmHost
            ? hostName +
              "  <size=90%><b><font=\"Barlow-BoldItalic SDF\" material=\"Barlow-BoldItalic SDF Outline\">" +
              translations.GetString(StringNames.HostYouLabel, Array.Empty<Object>())
            : $"{hostName} ({playerPreview.ColorBlindName})";
    }
}

[HarmonyPatch]
public static class TouRainbowCompatibilityPatch
{
    [HarmonyPrepare]
    public static bool Prepare() => TargetMethods().Any();

    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return new[]
        {
            AccessTools.TypeByName("TownOfUs.Modules.RainbowMod.RainbowBehaviour"),
            AccessTools.TypeByName("TownOfUs.Modules.RainbowMod.BasicRainbowBehaviour")
        }.Where(type => type != null)
            .Select(type => AccessTools.Method(type, "Update"))
            .Where(method => method != null)
            .Cast<MethodBase>();
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(object __instance)
    {
        var idField = AccessTools.Field(__instance.GetType(), "Id");
        return idField?.GetValue(__instance) is not int colorId || !AnimatedColorRenderer.IsAnimated(colorId);
    }
}
