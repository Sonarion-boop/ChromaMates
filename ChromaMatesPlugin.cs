using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MiraAPI;
using MiraAPI.PluginLoading;
using Reactor;
using Reactor.Utilities;
using System.Reflection;
using UnityEngine;

namespace ChromaMates;

[BepInAutoPlugin("local.sonarion.chromamates", "ChromaMates")]
[BepInProcess("Among Us.exe")]
[BepInDependency(ReactorPlugin.Id)]
[BepInDependency(MiraApiPlugin.Id)]
[BepInDependency("auavengers.tou.mira", BepInDependency.DependencyFlags.SoftDependency)]
public partial class ChromaMatesPlugin : BasePlugin, IMiraPlugin
{
    public const int NetworkProtocolVersion = 8;

    internal static ConfigEntry<int>? PreferredColorId { get; private set; }

    public Harmony Harmony { get; } = new(Id);

    public string OptionsTitleText => "ChromaMates";

    public ConfigFile GetConfigFile() => Config;

    public override void Load()
    {
        PreferredColorId = Config.Bind(
            "Local Customization",
            "PreferredColorId",
            -1,
            "The complete ChromaMates color ID selected in offline customization.");
        ReactorCredits.Register("ChromaMates", Version, false, ReactorCredits.AlwaysShow);
        Harmony.PatchAll();
        RemoveRedundantTouRainbowPatches();
        Log.LogInfo("Patches loaded. The color catalog will be built with the game's palette.");
    }

    private void RemoveRedundantTouRainbowPatches()
    {
        var patches = new (MethodBase? Original, string PatchType, string PatchMethod)[]
        {
            (
                AccessTools.Method(
                    typeof(PlayerMaterial),
                    nameof(PlayerMaterial.SetColors),
                    [typeof(int), typeof(Renderer)]),
                "TownOfUs.RainbowMod.SetPlayerMaterialPatch",
                "Prefix"),
            (
                AccessTools.Method(
                    typeof(PlayerMaterial),
                    nameof(PlayerMaterial.SetColors),
                    [typeof(Color), typeof(Renderer)]),
                "TownOfUs.RainbowMod.SetPlayerMaterialPatch2",
                "Prefix"),
            (
                AccessTools.Method(typeof(PlayerTab), nameof(PlayerTab.Update)),
                "TownOfUs.RainbowMod.PlayerTabPatch",
                "UpdatePostfix"),
            (
                AccessTools.Method(typeof(ChatNotification), nameof(ChatNotification.Update)),
                "TownOfUs.RainbowMod.ChatNotifRainbowPatch",
                "Prefix"),
            (
                AccessTools.Method(typeof(ChatNotification), nameof(ChatNotification.Update)),
                "TownOfUs.RainbowMod.ChatNotifRainbowPatch",
                "Postfix"),
            (
                AccessTools.Method(typeof(HostInfoPanel), nameof(HostInfoPanel.Update)),
                "TownOfUs.RainbowMod.RainbowLobbyInfoPanePatch",
                "Postfix")
        };

        var removed = 0;
        foreach (var (original, patchType, patchMethod) in patches)
        {
            var patch = AccessTools.Method(AccessTools.TypeByName(patchType), patchMethod);
            if (original == null || patch == null)
            {
                continue;
            }

            Harmony.Unpatch(original, patch);
            removed++;
        }

        if (removed > 0)
        {
            Log.LogInfo($"Using ChromaMates rendering in place of {removed} TOU:M rainbow hook(s).");
        }
    }
}

public enum ChromaMatesRpc : uint
{
    CatalogOfferV8 = 126,
    CatalogAcknowledgementV8,
    ExtendedColorRequestV8,
    ExtendedColorApplicationV8
}
