using System.Buffers.Binary;
using System.Text;

namespace VpnSample.Protocol;

sealed class PcapWriter : IDisposable
{
    const uint MagicNumber = 0xa1b2c3d4;
    const ushort MajorVersion = 2;
    const ushort MinorVersion = 4;
    const uint SnapshotLength = ushort.MaxValue;
    const uint RawIpLinkType = 101;

    readonly object writeLock = new();
    readonly FileStream stream;

    PcapWriter(string path)
    {
        FilePath = path;
        stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });

        Span<byte> header = stackalloc byte[24];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], SnapshotLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], RawIpLinkType);
        stream.Write(header);
    }

    public string FilePath { get; }

    public static PcapWriter Create(string configuredPath, string side)
    {
        string path = ResolvePath(configuredPath, side);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new PcapWriter(path);
    }

    public void Write(DateTimeOffset timestamp, ReadOnlySpan<byte> packet)
    {
        long ticksSinceEpoch = timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        uint seconds = checked((uint)(ticksSinceEpoch / TimeSpan.TicksPerSecond));
        uint microseconds = (uint)(ticksSinceEpoch % TimeSpan.TicksPerSecond / 10);
        uint packetLength = checked((uint)packet.Length);

        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], microseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], packetLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], packetLength);

        lock (writeLock)
        {
            stream.Write(header);
            stream.Write(packet);
        }
    }

    public void Dispose() => stream.Dispose();

    static string ResolvePath(string configuredPath, string side)
    {
        string safeSide = SanitizeFileName(side);
        string path;

        if (configuredPath.Contains("{side}", StringComparison.Ordinal))
        {
            path = configuredPath.Replace("{side}", safeSide, StringComparison.Ordinal);
        }
        else
        {
            string? directory = Path.GetDirectoryName(configuredPath);
            string extension = Path.GetExtension(configuredPath);
            string fileName = Path.GetFileNameWithoutExtension(configuredPath);
            path = Path.Combine(directory ?? string.Empty,
                $"{fileName}-{safeSide}{extension}");
        }

        return Path.GetFullPath(path);
    }

    static string SanitizeFileName(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
            result.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        return result.ToString();
    }
}
