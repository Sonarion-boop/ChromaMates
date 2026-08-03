using Hazel;
using Reactor.Networking.Attributes;
using Reactor.Networking.Rpc;

namespace ChromaMates;

internal readonly struct CatalogOffer(
    int protocolVersion,
    int generation,
    string fingerprint,
    bool compatibilityFallback,
    long animationEpochMilliseconds,
    int availableColorLimit)
{
    public int ProtocolVersion { get; } = protocolVersion;

    public int Generation { get; } = generation;

    public string Fingerprint { get; } = fingerprint;

    public bool CompatibilityFallback { get; } = compatibilityFallback;

    public long AnimationEpochMilliseconds { get; } = animationEpochMilliseconds;

    public int AvailableColorLimit { get; } = availableColorLimit;
}

internal readonly struct CatalogAcknowledgement(
    int protocolVersion,
    int generation,
    string fingerprint,
    bool compatible)
{
    public int ProtocolVersion { get; } = protocolVersion;

    public int Generation { get; } = generation;

    public string Fingerprint { get; } = fingerprint;

    public bool Compatible { get; } = compatible;
}

internal readonly struct ExtendedColorRequest(int colorId)
{
    public int ColorId { get; } = colorId;
}

internal readonly struct ExtendedColorApplication(byte playerId, int colorId)
{
    public byte PlayerId { get; } = playerId;

    public int ColorId { get; } = colorId;
}

[RegisterCustomRpc((uint)ChromaMatesRpc.CatalogOfferV8)]
internal sealed class CatalogOfferRpc(ChromaMatesPlugin plugin, uint id)
    : PlayerCustomRpc<ChromaMatesPlugin, CatalogOffer>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.None;

    public override void Write(MessageWriter writer, CatalogOffer data)
    {
        writer.Write(data.ProtocolVersion);
        writer.Write(data.Generation);
        writer.Write(data.Fingerprint);
        writer.Write(data.CompatibilityFallback);
        writer.Write(unchecked((ulong)data.AnimationEpochMilliseconds));
        writer.Write(data.AvailableColorLimit);
    }

    public override CatalogOffer Read(MessageReader reader)
    {
        return new CatalogOffer(
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadBoolean(),
            unchecked((long)reader.ReadUInt64()),
            reader.ReadInt32());
    }

    public override void Handle(PlayerControl sender, CatalogOffer data)
    {
        ColorNetwork.ReceiveCatalogOffer(sender, data);
    }
}

[RegisterCustomRpc((uint)ChromaMatesRpc.CatalogAcknowledgementV8)]
internal sealed class CatalogAcknowledgementRpc(ChromaMatesPlugin plugin, uint id)
    : PlayerCustomRpc<ChromaMatesPlugin, CatalogAcknowledgement>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.None;

    public override void Write(MessageWriter writer, CatalogAcknowledgement data)
    {
        writer.Write(data.ProtocolVersion);
        writer.Write(data.Generation);
        writer.Write(data.Fingerprint);
        writer.Write(data.Compatible);
    }

    public override CatalogAcknowledgement Read(MessageReader reader)
    {
        return new CatalogAcknowledgement(
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadBoolean());
    }

    public override void Handle(PlayerControl sender, CatalogAcknowledgement data)
    {
        ColorSynchronization.RecordAcknowledgement(
            sender,
            data.ProtocolVersion,
            data.Generation,
            data.Fingerprint,
            data.Compatible);
    }
}

[RegisterCustomRpc((uint)ChromaMatesRpc.ExtendedColorRequestV8)]
internal sealed class ExtendedColorRequestRpc(ChromaMatesPlugin plugin, uint id)
    : PlayerCustomRpc<ChromaMatesPlugin, ExtendedColorRequest>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.None;

    public override void Write(MessageWriter writer, ExtendedColorRequest data)
    {
        writer.Write(data.ColorId);
    }

    public override ExtendedColorRequest Read(MessageReader reader)
    {
        return new ExtendedColorRequest(reader.ReadInt32());
    }

    public override void Handle(PlayerControl sender, ExtendedColorRequest data)
    {
        ColorNetwork.ApplyRequestedColorAsHost(sender, data.ColorId);
    }
}

[RegisterCustomRpc((uint)ChromaMatesRpc.ExtendedColorApplicationV8)]
internal sealed class ExtendedColorApplicationRpc(ChromaMatesPlugin plugin, uint id)
    : PlayerCustomRpc<ChromaMatesPlugin, ExtendedColorApplication>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.None;

    public override void Write(MessageWriter writer, ExtendedColorApplication data)
    {
        writer.Write(data.PlayerId);
        writer.Write(data.ColorId);
    }

    public override ExtendedColorApplication Read(MessageReader reader)
    {
        return new ExtendedColorApplication(reader.ReadByte(), reader.ReadInt32());
    }

    public override void Handle(PlayerControl sender, ExtendedColorApplication data)
    {
        ColorNetwork.ReceiveExtendedColor(sender, data);
    }
}
