using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VpnSample.Protocol;

public static class HttpsTunnelTransport
{
    const int MaximumHeaderLength = 16 * 1024;
    const string UpgradePath = "/vpn";
    const string UpgradeToken = "vpnsample/3";

    public static async Task<SslStream> ConnectAsync(
        Stream transport,
        string serverName,
        X509Certificate2? pinnedCertificate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        RemoteCertificateValidationCallback? validation = pinnedCertificate is null
            ? null
            : (_, certificate, _, _) => MatchesPin(certificate, pinnedCertificate);
        var tls = new SslStream(transport, leaveInnerStreamOpen: false, validation);

        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            EnsureHttp11(tls);

            string request =
                $"GET {UpgradePath} HTTP/1.1\r\n" +
                $"Host: {serverName}\r\n" +
                "User-Agent: VPNSample/3\r\n" +
                "Connection: Upgrade\r\n" +
                $"Upgrade: {UpgradeToken}\r\n\r\n";
            await WriteHeaderAsync(tls, request, cancellationToken);

            string response = await ReadHeaderAsync(tls, cancellationToken);
            ValidateUpgradeResponse(response);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync();
            throw;
        }
    }

    public static async Task<SslStream> AcceptAsync(
        Stream transport,
        X509Certificate2 certificate,
        string expectedServerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerName);
        var tls = new SslStream(transport, leaveInnerStreamOpen: false);

        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            EnsureHttp11(tls);

            string request = await ReadHeaderAsync(tls, cancellationToken);
            ValidateUpgradeRequest(request, expectedServerName);
            await WriteHeaderAsync(
                tls,
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                $"Upgrade: {UpgradeToken}\r\n\r\n",
                cancellationToken);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync();
            throw;
        }
    }

    static bool MatchesPin(X509Certificate? certificate, X509Certificate2 pinnedCertificate)
    {
        if (certificate is null)
            return false;
        using var presented = new X509Certificate2(certificate);
        return CryptographicOperations.FixedTimeEquals(
            presented.GetCertHash(HashAlgorithmName.SHA256),
            pinnedCertificate.GetCertHash(HashAlgorithmName.SHA256));
    }

    static void EnsureHttp11(SslStream tls)
    {
        if (tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http11)
            throw new AuthenticationException("The peer did not negotiate HTTP/1.1 over TLS.");
    }

    static async Task WriteHeaderAsync(
        Stream transport,
        string header,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(header);
        await transport.WriteAsync(bytes, cancellationToken);
        await transport.FlushAsync(cancellationToken);
    }

    static async Task<string> ReadHeaderAsync(Stream transport, CancellationToken cancellationToken)
    {
        var header = new byte[MaximumHeaderLength];
        int length = 0;
        while (length < header.Length)
        {
            int read = await transport.ReadAsync(header.AsMemory(length, 1), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("HTTPS peer closed before completing its headers.");
            length += read;
            if (length >= 4 && header.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                return Encoding.ASCII.GetString(header, 0, length);
        }

        throw new InvalidDataException($"HTTPS headers exceeded {MaximumHeaderLength} bytes.");
    }

    static void ValidateUpgradeRequest(string request, string expectedServerName)
    {
        string[] lines = request.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2 || lines[0] != $"GET {UpgradePath} HTTP/1.1")
            throw new InvalidDataException("Expected an HTTPS GET /vpn upgrade request.");
        if (!HasHeader(lines, "Host", expectedServerName))
            throw new InvalidDataException($"Expected HTTPS Host '{expectedServerName}'.");
        if (!HasTokenHeader(lines, "Connection", "Upgrade") ||
            !HasHeader(lines, "Upgrade", UpgradeToken))
        {
            throw new InvalidDataException("Expected an HTTPS VPNSample upgrade request.");
        }
    }

    static void ValidateUpgradeResponse(string response)
    {
        string[] lines = response.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2 || lines[0] != "HTTP/1.1 101 Switching Protocols")
            throw new InvalidDataException("HTTPS endpoint did not accept the VPN upgrade.");
        if (!HasTokenHeader(lines, "Connection", "Upgrade") ||
            !HasHeader(lines, "Upgrade", UpgradeToken))
        {
            throw new InvalidDataException("HTTPS endpoint returned an invalid VPN upgrade response.");
        }
    }

    static bool HasHeader(string[] lines, string name, string expectedValue)
    {
        string prefix = name + ":";
        return lines.Any(line =>
            line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            line[prefix.Length..].Trim().Equals(expectedValue, StringComparison.OrdinalIgnoreCase));
    }

    static bool HasTokenHeader(string[] lines, string name, string expectedToken)
    {
        string prefix = name + ":";
        return lines
            .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => line[prefix.Length..].Split(','))
            .Any(token => token.Trim().Equals(expectedToken, StringComparison.OrdinalIgnoreCase));
    }
}
