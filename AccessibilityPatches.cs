using AmongUs.Data;
using HarmonyLib;
using Reactor.Utilities;

namespace ChromaMates;

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
public static class AccessibilityPatches
{
    [HarmonyPostfix]
    public static void EnableColorNames()
    {
        var accessibility = DataManager.Settings?.Accessibility;
        if (accessibility == null)
        {
            return;
        }

        var wasEnabled = accessibility.ColorBlindMode;
        accessibility.ColorBlindMode = true;
        Logger<ChromaMatesPlugin>.Info(
            wasEnabled
                ? "Color names were already turned on."
                : "Turned on color names so the expanded palette stays readable.");
    }
}
