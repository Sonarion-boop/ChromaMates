using System.Collections;
using System.Reflection;
using HarmonyLib;
using Reactor.Networking;

namespace ChromaMates;

internal static class RemoteModCompatibility
{
    private static readonly Type? ClientDataType =
        AccessTools.TypeByName("Reactor.Networking.Patches.ReactorClientData");
    private static readonly MethodInfo? GetClientDataMethod =
        AccessTools.Method(ClientDataType, "Get", [typeof(int)]);
    private static readonly PropertyInfo? ModsProperty =
        AccessTools.Property(ClientDataType, "Mods");
    private static bool _reflectionFailureLogged;

    public static bool HasChromaMates(int clientId)
    {
        if (GetClientDataMethod == null || ModsProperty == null)
        {
            LogReflectionFailureOnce("Reactor's remote mod-list API was not found.");
            return false;
        }

        try
        {
            var remoteClient = GetClientDataMethod.Invoke(null, [clientId]);
            if (remoteClient == null || ModsProperty.GetValue(remoteClient) is not IEnumerable mods)
            {
                return false;
            }

            foreach (var entry in mods)
            {
                if (entry is Mod mod &&
                    string.Equals(mod.Id, ChromaMatesPlugin.Id, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            LogReflectionFailureOnce(
                $"Reactor's remote mod list could not be read: {exception.GetType().Name}.");
        }

        return false;
    }

    private static void LogReflectionFailureOnce(string message)
    {
        if (_reflectionFailureLogged)
        {
            return;
        }

        _reflectionFailureLogged = true;
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Warning(
            $"{message} ChromaMates will remain network-silent for safety.");
    }
}
