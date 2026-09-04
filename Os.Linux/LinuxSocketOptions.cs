using System.Net.Sockets;

namespace VpnSample.Os;

public static class LinuxSocketOptions
{
    const int SolSocket = 1;
    const int SoMark = 36;

    public static void SetMark(Socket socket, int mark)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Socket marks are only available on Linux.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mark);

        Span<byte> value = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(value, mark);
        socket.SetRawSocketOption(SolSocket, SoMark, value);
    }
}
