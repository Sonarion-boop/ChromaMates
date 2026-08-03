using ChromaMates.Colors;
using Reactor.Networking.Rpc;

namespace ChromaMates;

public static class ColorNetwork
{
    internal static void SendCatalogOffer(
        int targetClientId,
        int generation,
        string fingerprint,
        bool compatibilityFallback,
        int availableColorLimit)
    {
        if (!AmongUsClient.Instance ||
            !AmongUsClient.Instance.AmHost ||
            !RemoteModCompatibility.HasChromaMates(targetClientId))
        {
            return;
        }

        var epochMilliseconds = (long)Math.Round(
            ColorCatalog.SynchronizedTime * 1_000d,
            MidpointRounding.AwayFromZero);
        Rpc<CatalogOfferRpc>.Instance.SendTo(
            targetClientId,
            new CatalogOffer(
                ChromaMatesPlugin.NetworkProtocolVersion,
                generation,
                fingerprint,
                compatibilityFallback,
                epochMilliseconds,
                availableColorLimit));
    }

    internal static void ReceiveCatalogOffer(PlayerControl host, CatalogOffer offer)
    {
        if (!IsAuthoritativeHostPlayer(host) || !ColorCatalog.IsFinalized)
        {
            return;
        }

        ColorSynchronization.ConfirmRemoteHostCapability();
        ColorCatalog.SynchronizeAnimationEpoch(
            offer.AnimationEpochMilliseconds / 1_000d);
        var localFingerprint = ColorCatalog.GetLiveFingerprint(offer.AvailableColorLimit);
        var compatible =
            offer.ProtocolVersion == ChromaMatesPlugin.NetworkProtocolVersion &&
            string.Equals(
                localFingerprint,
                offer.Fingerprint,
                StringComparison.Ordinal);
        if (compatible || offer.CompatibilityFallback)
        {
            ColorAvailability.SetSyncedLimit(offer.AvailableColorLimit);
        }

        ColorSynchronization.ReceiveCatalogOffer(
            offer.Generation,
            offer.CompatibilityFallback,
            compatible);

        if (compatible)
        {
            Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
                $"Host catalog accepted: handshake {offer.Generation}, " +
                $"{offer.AvailableColorLimit} colors, compatibility set " +
                $"{offer.CompatibilityFallback}.");
        }

        SendCatalogAcknowledgement(
            offer.Generation,
            localFingerprint,
            compatible);

        if (compatible)
        {
            ColorAvailability.RequestRememberedColorFromHost(offer.Generation);
            return;
        }

        Reactor.Utilities.Logger<ChromaMatesPlugin>.Warning(
            $"Host catalog does not match: protocol {offer.ProtocolVersion}, " +
            $"host {offer.Fingerprint}, local {localFingerprint}, " +
            $"limit {offer.AvailableColorLimit}.");
        ShowLocalSystemMessage(
            "<color=#C979FF>ChromaMates</color>",
            "<color=#FF6060>Your color catalog differs from the host. " +
            "Current colors will stay visible, but some color choices may not " +
            "be available until the catalogs match.</color>");
    }

    internal static void SendCatalogAcknowledgement(
        int generation,
        string localFingerprint,
        bool compatible)
    {
        if (!ColorSynchronization.HasConfirmedRemoteHost ||
            !AmongUsClient.Instance ||
            AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer?.Data is not { Disconnected: false })
        {
            return;
        }

        Rpc<CatalogAcknowledgementRpc>.Instance.SendTo(
            AmongUsClient.Instance.HostId,
            new CatalogAcknowledgement(
                ChromaMatesPlugin.NetworkProtocolVersion,
                generation,
                localFingerprint,
                compatible));
    }

    public static void RpcRequestExtendedColor(
        PlayerControl requester,
        int requestedColorId)
    {
        if (!AmongUsClient.Instance ||
            requester?.Data is not { Disconnected: false })
        {
            return;
        }

        if (AmongUsClient.Instance.AmHost)
        {
            ApplyRequestedColorAsHost(requester, requestedColorId);
            return;
        }

        if (!ColorSynchronization.HasConfirmedRemoteHost ||
            PlayerControl.LocalPlayer == null ||
            requester.PlayerId != PlayerControl.LocalPlayer.PlayerId)
        {
            return;
        }

        Rpc<ExtendedColorRequestRpc>.Instance.SendTo(
            AmongUsClient.Instance.HostId,
            new ExtendedColorRequest(requestedColorId));
    }

    internal static void ApplyRequestedColorAsHost(
        PlayerControl requester,
        int requestedColorId)
    {
        if (!AmongUsClient.Instance ||
            !AmongUsClient.Instance.AmHost ||
            requester?.Data is not { Disconnected: false })
        {
            return;
        }

        if ((PlayerControl.LocalPlayer == null ||
             requester.PlayerId != PlayerControl.LocalPlayer.PlayerId) &&
            (!TryGetClientId(requester, out var requesterClientId) ||
             !RemoteModCompatibility.HasChromaMates(requesterClientId)))
        {
            return;
        }

        var occupied = PlayerControl.AllPlayerControls.ToArray()
            .Where(player =>
                player?.Data is { Disconnected: false } &&
                player.PlayerId != requester.PlayerId)
            .Select(player => player.Data.DefaultOutfit.ColorId)
            .ToHashSet();
        var selected = ColorAvailability.IsAllowed(requestedColorId) &&
                       !occupied.Contains(requestedColorId)
            ? requestedColorId
            : ColorAvailability.FindNearestAllowedColorId(requestedColorId, occupied);
        if (!selected.HasValue)
        {
            ShowLocalSystemMessage(
                "<color=#C979FF>ChromaMates</color>",
                "No unique color is available for that player.");
            return;
        }

        ColorAvailability.ApplyHostColor(requester, selected.Value);
    }

    public static void RpcApplyExtendedColor(
        PlayerControl host,
        PlayerControl target,
        int colorId)
    {
        if (!AmongUsClient.Instance ||
            !AmongUsClient.Instance.AmHost ||
            !IsAuthoritativeHostPlayer(host) ||
            target?.Data is not { Disconnected: false } ||
            !ColorAvailability.IsRenderableCatalogColor(colorId))
        {
            return;
        }

        ApplyExtendedColor(target, colorId);
        var application = new ExtendedColorApplication(target.PlayerId, colorId);
        foreach (var client in AmongUsClient.Instance.allClients.ToArray())
        {
            if (client == null ||
                client.Id == AmongUsClient.Instance.HostId ||
                !RemoteModCompatibility.HasChromaMates(client.Id))
            {
                continue;
            }

            Rpc<ExtendedColorApplicationRpc>.Instance.SendTo(client.Id, application);
        }
    }

    internal static void ReceiveExtendedColor(
        PlayerControl host,
        ExtendedColorApplication application)
    {
        if (!ColorSynchronization.HasConfirmedRemoteHost ||
            !IsAuthoritativeHostPlayer(host))
        {
            return;
        }

        var target = PlayerControl.AllPlayerControls.ToArray()
            .FirstOrDefault(player => player != null && player.PlayerId == application.PlayerId);
        if (target?.Data is not { Disconnected: false } ||
            !ColorAvailability.IsRenderableCatalogColor(application.ColorId))
        {
            return;
        }

        ApplyExtendedColor(target, application.ColorId);
    }

    internal static bool IsAuthoritativeHostPlayer(PlayerControl? player)
    {
        if (player == null || !AmongUsClient.Instance)
        {
            return false;
        }

        return AmongUsClient.Instance.allClients.ToArray().Any(client =>
            client?.Character != null &&
            client.Character.PlayerId == player.PlayerId &&
            client.Id == AmongUsClient.Instance.HostId);
    }

    internal static void ShowLocalSystemMessage(string title, string message)
    {
        if (!HudManager.InstanceExists || PlayerControl.LocalPlayer == null)
        {
            return;
        }

        HudManager.Instance.Chat.AddChat(
            PlayerControl.LocalPlayer,
            $"{title}\n{message}",
            false);
    }

    private static void ApplyExtendedColor(PlayerControl target, int colorId)
    {
        var previousColorId = target.Data.DefaultOutfit.ColorId;
        target.RawSetColor(colorId);
        if (PlayerControl.LocalPlayer != null &&
            target.PlayerId == PlayerControl.LocalPlayer.PlayerId)
        {
            ColorAvailability.RememberPreferredColor(colorId);
            ColorSelectorTabs.ApplyEquippedSelection(
                colorId,
                previousColorId,
                refreshNetworkAvailability: true);
        }
    }

    private static bool TryGetClientId(PlayerControl player, out int clientId)
    {
        clientId = -1;
        if (!AmongUsClient.Instance)
        {
            return false;
        }

        var client = AmongUsClient.Instance.allClients.ToArray().FirstOrDefault(candidate =>
            candidate?.Character != null &&
            candidate.Character.PlayerId == player.PlayerId);
        if (client == null)
        {
            return false;
        }

        clientId = client.Id;
        return true;
    }
}
