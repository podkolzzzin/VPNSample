using System.Security.Cryptography;
using System.Text;

namespace VpnSample.Protocol;

enum PacketFlow
{
    Send,
    Receive
}

sealed class PacketTrace : IDisposable
{
    const string PacketsVariable = "VPN_TRACE_PACKETS";
    const string HexVariable = "VPN_TRACE_HEX";
    const string PcapVariable = "VPN_TRACE_PCAP";

    static readonly object ConsoleLock = new();

    readonly string side;
    readonly bool writeSummary;
    readonly bool writeHex;
    readonly PcapWriter? pcap;

    public PacketTrace(string side)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(side);

        this.side = side;
        writeHex = IsEnabled(HexVariable);
        writeSummary = writeHex || IsEnabled(PacketsVariable);

        string? pcapPath = Environment.GetEnvironmentVariable(PcapVariable);
        if (!string.IsNullOrWhiteSpace(pcapPath))
        {
            pcap = PcapWriter.Create(pcapPath, side);
            WriteConsole($"Packet capture [{side}]: {pcap.FilePath}{Environment.NewLine}");
        }
    }

    public void Write(PacketFlow flow, ReadOnlySpan<byte> packet)
    {
        if (!writeSummary && pcap is null)
            return;

        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        pcap?.Write(timestamp, packet);

        if (!writeSummary)
            return;

        var output = new StringBuilder(FormatSummary(timestamp, flow, packet));
        output.AppendLine();
        if (writeHex)
            AppendHexDump(output, packet);

        WriteConsole(output.ToString());
    }

    public void Dispose() => pcap?.Dispose();

    string FormatSummary(
        DateTimeOffset timestamp,
        PacketFlow flow,
        ReadOnlySpan<byte> packet)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(packet, hash);
        string packetId = Convert.ToHexString(hash[..8]);
        string direction = flow == PacketFlow.Send ? "SEND" : "RECV";

        return $"{timestamp:HH:mm:ss.fff'Z'} [{side}] {direction} #{packetId}  " +
            $"{IpPacketFormatter.Format(packet)}  {packet.Length} B";
    }

    static void AppendHexDump(StringBuilder output, ReadOnlySpan<byte> packet)
    {
        for (int offset = 0; offset < packet.Length; offset += 16)
        {
            ReadOnlySpan<byte> row = packet.Slice(offset, Math.Min(16, packet.Length - offset));
            output.Append("  ").Append(offset.ToString("X4")).Append("  ");

            for (int index = 0; index < 16; index++)
            {
                output.Append(index < row.Length ? row[index].ToString("X2") : "  ");
                output.Append(index == 7 ? "  " : " ");
            }

            output.Append(" | ");
            foreach (byte value in row)
                output.Append(value is >= 0x20 and <= 0x7e ? (char)value : '.');
            output.AppendLine();
        }
    }

    static bool IsEnabled(string variable) =>
        Environment.GetEnvironmentVariable(variable) == "1";

    static void WriteConsole(string output)
    {
        lock (ConsoleLock)
            Console.Error.Write(output);
    }
}
