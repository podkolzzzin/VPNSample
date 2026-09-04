using System.Text;

namespace VpnSample.Mesh;

public static class MeshSessionProtocol
{
    public const int TokenLength = 32;

    public static async Task WriteAsync(
        Stream transport,
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        Validate(sessionToken);
        await transport.WriteAsync(Encoding.ASCII.GetBytes(sessionToken), cancellationToken);
    }

    public static async Task<string> ReadAsync(
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        var token = new byte[TokenLength];
        await transport.ReadExactlyAsync(token, cancellationToken);
        string value = Encoding.ASCII.GetString(token);
        Validate(value);
        return value;
    }

    public static void Validate(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        if (sessionToken.Length != TokenLength ||
            sessionToken.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A mesh session token must contain 32 lowercase hex characters.",
                nameof(sessionToken));
        }
    }
}
