using HarmonyLib;

namespace ChromaMates;

[HarmonyPatch]
public static class ColorRuntimePatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
    public static void ExitGamePostfix()
    {
        ColorSynchronization.Reset();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
    public static void GameStartManagerUpdatePostfix()
    {
        try
        {
            ColorSynchronization.TickLobby();
        }
        catch (Exception exception)
        {
            Reactor.Utilities.Logger<ChromaMatesPlugin>.Error(
                $"Lobby color synchronization failed safely: {exception}");
        }
    }
}

[HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.Update))]
public static class WardrobePreviewRefreshPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ColorSelectorTabs.RefreshHighlightedPreview();
    }
}
