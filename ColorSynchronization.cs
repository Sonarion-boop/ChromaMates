using InnerNet;
using ChromaMates.Colors;
using UnityEngine;

namespace ChromaMates;

internal enum CatalogClientState
{
    Awaiting,
    Compatible,
    Incompatible,
    Missing
}

public static class ColorSynchronization
{
    private const float PollInterval = 0.5f;
    private const float OfferRetryInterval = 1f;
    private const float MissingOfferRetryInterval = 5f;
    private const float VerifiedOfferInterval = 5f;
    private const float NegotiationTimeout = 10f;
    private static readonly Dictionary<int, CatalogClientState> ClientStates = [];
    private static readonly Dictionary<int, string> ClientFingerprints = [];
    private static string _rosterSignature = string.Empty;
    private static float _nextPollTime;
    private static float _negotiationStartedAt;
    private static float _nextOfferTime;
    private static int _generation;
    private static int _lastConfiguredLimit = -1;
    private static int _offeredLimit;
    private static string _offeredFingerprint = string.Empty;
    private static bool _vanillaFallback;
    private static bool _localFallbackNoticeShown;
    private static int _confirmedHostGameId = int.MinValue;
    private static int _confirmedHostClientId = int.MinValue;

    public static bool VanillaFallbackActive => _vanillaFallback;

    internal static bool HasConfirmedRemoteHost =>
        AmongUsClient.Instance &&
        !AmongUsClient.Instance.AmHost &&
        AmongUsClient.Instance.GameId == _confirmedHostGameId &&
        AmongUsClient.Instance.HostId == _confirmedHostClientId;

    public static void TickLobby()
    {
        if (!AmongUsClient.Instance)
        {
            return;
        }

        if (AmongUsClient.Instance.AmHost)
        {
            TickHost();
            return;
        }

        TickClient();
    }

    public static void TickHost(bool force = false)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost)
        {
            return;
        }
        if (!ColorCatalog.IsFinalized)
        {
            return;
        }
        if (!force && Time.unscaledTime < _nextPollTime)
        {
            return;
        }

        _nextPollTime = Time.unscaledTime + PollInterval;
        ColorAvailability.ApplyRememberedHostColor();
        var configuredLimit = ColorAvailability.GetHostConfiguredLimit();
        var configuredLimitChanged = _lastConfiguredLimit != configuredLimit;
        if (configuredLimitChanged)
        {
            _lastConfiguredLimit = configuredLimit;
            ColorSelectorTabs.RefreshForNetworkChange();
            ColorAvailability.EnforceRoster(CanUseHiddenFallback());
        }

        var clients = GetConnectedClients();
        var signature = string.Join(
            ",",
            clients
                .OrderBy(client => client.Id)
                .Select(client =>
                    $"{client.Id}:{client.Character.PlayerId}:{client.PlayerName}:" +
                    RemoteModCompatibility.HasChromaMates(client.Id))) +
            $"|{AmongUsClient.Instance.GameId}|{AmongUsClient.Instance.HostId}" +
            $"|{configuredLimit}|{ColorCatalog.Fingerprint}";
        var rosterChanged =
            !string.Equals(signature, _rosterSignature, StringComparison.Ordinal);
        if (rosterChanged)
        {
            _rosterSignature = signature;
            if (!configuredLimitChanged)
            {
                ColorAvailability.EnforceRoster(useFortegreenForInvalid: false);
            }
        }

        if (configuredLimit <= ColorAvailability.MiraColorCount)
        {
            EnterClientOptionalMode();
            return;
        }

        if (rosterChanged)
        {
            StartNegotiation(clients);
            return;
        }

        if (_vanillaFallback)
        {
            return;
        }

        var hasAwaiting = ClientStates.Values.Any(
            state => state == CatalogClientState.Awaiting);
        var hasMissing = ClientStates.Values.Any(
            state => state == CatalogClientState.Missing);
        if (!hasAwaiting && !hasMissing)
        {
            if (Time.unscaledTime >= _nextOfferTime)
            {
                _nextOfferTime = Time.unscaledTime + VerifiedOfferInterval;
                SendCurrentOffer();
            }
            return;
        }

        if (hasAwaiting &&
            Time.unscaledTime - _negotiationStartedAt >= NegotiationTimeout)
        {
            foreach (var clientId in ClientStates
                         .Where(entry => entry.Value == CatalogClientState.Awaiting)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                ClientStates[clientId] = CatalogClientState.Missing;
            }
            _nextOfferTime = Time.unscaledTime + MissingOfferRetryInterval;
            return;
        }

        if (Time.unscaledTime >= _nextOfferTime)
        {
            _nextOfferTime = Time.unscaledTime +
                             (hasAwaiting
                                 ? OfferRetryInterval
                                 : MissingOfferRetryInterval);
            SendCurrentOffer();
        }
    }

    public static void Reset()
    {
        ClientStates.Clear();
        ClientFingerprints.Clear();
        _rosterSignature = string.Empty;
        _nextPollTime = 0f;
        _negotiationStartedAt = 0f;
        _nextOfferTime = 0f;
        _generation = 0;
        _lastConfiguredLimit = -1;
        _offeredLimit = 0;
        _offeredFingerprint = string.Empty;
        _vanillaFallback = false;
        _localFallbackNoticeShown = false;
        _confirmedHostGameId = int.MinValue;
        _confirmedHostClientId = int.MinValue;
        ColorAvailability.SetHostCompatibilityLimit(null);
        ColorAvailability.SetSyncedLimit(ColorAvailability.MiraColorCount);
        ColorAvailability.ResetPreferredColorRequest();
    }

    internal static void ConfirmRemoteHostCapability()
    {
        if (!AmongUsClient.Instance || AmongUsClient.Instance.AmHost)
        {
            return;
        }

        _confirmedHostGameId = AmongUsClient.Instance.GameId;
        _confirmedHostClientId = AmongUsClient.Instance.HostId;
    }

    public static void ReceiveCatalogOffer(
        int generation,
        bool vanillaFallback,
        bool compatible)
    {
        if (generation < _generation)
        {
            return;
        }

        _generation = generation;
        _vanillaFallback = vanillaFallback;
        if (vanillaFallback && compatible && !_localFallbackNoticeShown)
        {
            _localFallbackNoticeShown = true;
            ColorNetwork.ShowLocalSystemMessage(
                "<color=#C979FF>ChromaMates</color>",
                "Catalogs differ, so this lobby is safely using the 52 vanilla + TOU:M colors.");
        }
        else if (!vanillaFallback)
        {
            _localFallbackNoticeShown = false;
        }
    }

    public static void RecordAcknowledgement(
        PlayerControl responder,
        int protocolVersion,
        int generation,
        string localFingerprint,
        bool compatible)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost ||
            protocolVersion != ChromaMatesPlugin.NetworkProtocolVersion ||
            generation != _generation ||
            !TryGetClientId(responder, out var clientId) ||
            !RemoteModCompatibility.HasChromaMates(clientId))
        {
            return;
        }

        var verifiedCompatible = compatible &&
                                 string.Equals(
                                     localFingerprint,
                                     _offeredFingerprint,
                                     StringComparison.Ordinal);
        ClientStates[clientId] = verifiedCompatible
            ? CatalogClientState.Compatible
            : CatalogClientState.Incompatible;
        ClientFingerprints[clientId] = localFingerprint;
        if (!verifiedCompatible)
        {
            var client = GetConnectedClients().FirstOrDefault(entry => entry.Id == clientId);
            var clientName = client == null ? $"client {clientId}" : GetClientName(client);
            Reactor.Utilities.Logger<ChromaMatesPlugin>.Warning(
                $"Catalog mismatch from {clientName}: protocol {protocolVersion}, " +
                $"remote {localFingerprint}, host " +
                $"{_offeredFingerprint}, limit {_offeredLimit}, generation {_generation}.");
        }
        else
        {
            Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
                $"Verified the color catalog for client {clientId} at limit " +
                $"{_offeredLimit}, generation {_generation}.");
        }
    }

    private static void StartNegotiation(IReadOnlyCollection<ClientData> clients)
    {
        _generation++;
        _vanillaFallback = false;
        ClientStates.Clear();
        ClientFingerprints.Clear();
        _negotiationStartedAt = Time.unscaledTime;
        _nextOfferTime = Time.unscaledTime + OfferRetryInterval;
        foreach (var client in clients.Where(client =>
                     RemoteModCompatibility.HasChromaMates(client.Id)))
        {
            ClientStates[client.Id] = CatalogClientState.Awaiting;
        }

        ColorAvailability.SetHostCompatibilityLimit(null);
        _offeredLimit = ColorAvailability.GetHostConfiguredLimit();
        _offeredFingerprint = ColorCatalog.GetFingerprint(_offeredLimit);
        Reactor.Utilities.Logger<ChromaMatesPlugin>.Info(
            $"Catalog handshake {_generation} started for {ClientStates.Count} " +
            $"confirmed ChromaMates client(s) with {_offeredLimit} colors. " +
            $"{clients.Count - ClientStates.Count} client(s) will receive no " +
            "ChromaMates traffic.");

        SendCurrentOffer();
    }

    private static void SendCurrentOffer()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost ||
            !ColorCatalog.IsFinalized ||
            PlayerControl.LocalPlayer == null ||
            ColorAvailability.GetHostConfiguredLimit() <=
            ColorAvailability.MiraColorCount)
        {
            return;
        }

        foreach (var clientId in ClientStates.Keys.ToArray())
        {
            if (!RemoteModCompatibility.HasChromaMates(clientId))
            {
                ClientStates.Remove(clientId);
                ClientFingerprints.Remove(clientId);
                continue;
            }

            ColorNetwork.SendCatalogOffer(
                clientId,
                _generation,
                _offeredFingerprint,
                _vanillaFallback,
                _offeredLimit);
        }
    }

    private static void EnterClientOptionalMode()
    {
        ClientStates.Clear();
        ClientFingerprints.Clear();
        _generation = 0;
        _offeredLimit = 0;
        _offeredFingerprint = string.Empty;
        _vanillaFallback = false;
        _localFallbackNoticeShown = false;
        _negotiationStartedAt = 0f;
        _nextOfferTime = 0f;
        ColorAvailability.SetHostCompatibilityLimit(null);
        ColorAvailability.SetSyncedLimit(ColorAvailability.MiraColorCount);
    }

    private static List<ClientData> GetConnectedClients()
    {
        if (!AmongUsClient.Instance)
        {
            return [];
        }

        return AmongUsClient.Instance.allClients.ToArray()
            .Where(client =>
                client?.Character?.Data is { Disconnected: false } &&
                client.Id != AmongUsClient.Instance.HostId)
            .ToList();
    }

    private static bool CanUseHiddenFallback()
    {
        return GetConnectedClients().All(client =>
            ClientStates.TryGetValue(client.Id, out var state) &&
            state is CatalogClientState.Compatible or CatalogClientState.Incompatible);
    }

    private static bool TryGetClientId(PlayerControl player, out int clientId)
    {
        clientId = -1;
        if (player == null || !AmongUsClient.Instance)
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

    private static void TickClient()
    {
        // The host initiates synchronization after Reactor confirms that both
        // endpoints have ChromaMates. A client never probes an unknown host.
    }

    private static string GetClientName(ClientData client)
    {
        return string.IsNullOrWhiteSpace(client.PlayerName)
            ? $"client {client.Id}"
            : client.PlayerName;
    }

}
