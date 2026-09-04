using System.Text;

namespace VpnSample.Dns;

public static class NodeRegistrationProtocol
{
    const byte Version = 1;
    const byte Accepted = 0;
    const byte NameInUse = 1;

    public static async Task WriteRequestAsync(
        Stream transport,
        string nodeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        string normalized = DnsName.NormalizeNodeName(nodeName);
        byte[] nameBytes = Encoding.ASCII.GetBytes(normalized);
        var request = new byte[nameBytes.Length + 2];
        request[0] = Version;
        request[1] = checked((byte)nameBytes.Length);
        nameBytes.CopyTo(request, 2);
        await transport.WriteAsync(request, cancellationToken);
    }

    public static async Task<string> ReadRequestAsync(
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var header = new byte[2];
        await transport.ReadExactlyAsync(header, cancellationToken);
        if (header[0] != Version)
            throw new InvalidDataException($"Unsupported node registration version {header[0]}.");
        if (header[1] is 0 or > 63)
            throw new InvalidDataException("The node registration contains an invalid name length.");

        var nameBytes = new byte[header[1]];
        await transport.ReadExactlyAsync(nameBytes, cancellationToken);
        if (nameBytes.Any(value => value > 0x7f))
            throw new InvalidDataException("The node registration name must be ASCII.");

        try
        {
            return DnsName.NormalizeNodeName(Encoding.ASCII.GetString(nameBytes));
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("The node registration contains an invalid DNS label.", error);
        }
    }

    public static Task WriteAcceptedAsync(
        Stream transport,
        int clientNumber,
        CancellationToken cancellationToken = default) =>
        WriteResponseAsync(transport, Accepted, clientNumber, cancellationToken);

    public static Task WriteNameInUseAsync(
        Stream transport,
        CancellationToken cancellationToken = default) =>
        WriteResponseAsync(transport, NameInUse, 0, cancellationToken);

    public static async Task<int> ReadResponseAsync(
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var response = new byte[3];
        await transport.ReadExactlyAsync(response, cancellationToken);
        if (response[0] != Version)
            throw new InvalidDataException($"Unsupported node registration version {response[0]}.");
        return response[1] switch
        {
            Accepted => response[2],
            NameInUse => throw new InvalidOperationException("The requested VPN node name is already in use."),
            _ => throw new InvalidDataException($"Unknown node registration status {response[1]}.")
        };
    }

    static async Task WriteResponseAsync(
        Stream transport,
        byte status,
        int clientNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (clientNumber is < 0 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(clientNumber));
        await transport.WriteAsync(
            new[] { Version, status, checked((byte)clientNumber) },
            cancellationToken);
    }
}
