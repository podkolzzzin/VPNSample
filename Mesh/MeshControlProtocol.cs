using System.Buffers.Binary;
using System.Text.Json;

namespace VpnSample.Mesh;

public static class MeshControlProtocol
{
    public const string Path = "/api/v1/mesh";
    public const string SessionHeader = "X-VPNSample-Session";
    const int MaximumMessageLength = 256 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteRegistrationAsync(
        Stream transport,
        MeshRegistrationRequest registration,
        CancellationToken cancellationToken = default) =>
        WriteAsync(transport, registration, cancellationToken);

    public static Task<MeshRegistrationRequest> ReadRegistrationAsync(
        Stream transport,
        CancellationToken cancellationToken = default) =>
        ReadAsync<MeshRegistrationRequest>(transport, cancellationToken);

    public static Task WriteSnapshotAsync(
        Stream transport,
        MeshSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        WriteAsync(transport, snapshot, cancellationToken);

    public static Task<MeshSnapshot> ReadSnapshotAsync(
        Stream transport,
        CancellationToken cancellationToken = default) =>
        ReadAsync<MeshSnapshot>(transport, cancellationToken);

    static async Task WriteAsync<T>(
        Stream transport,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (body.Length > MaximumMessageLength)
            throw new InvalidDataException("A mesh control message is too large.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await transport.WriteAsync(header, cancellationToken);
        await transport.WriteAsync(body, cancellationToken);
    }

    static async Task<T> ReadAsync<T>(
        Stream transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var header = new byte[sizeof(int)];
        await transport.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumMessageLength)
            throw new InvalidDataException("A mesh control message has an invalid length.");

        var body = new byte[length];
        await transport.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions) ??
            throw new InvalidDataException("A mesh control message contains invalid JSON.");
    }
}
