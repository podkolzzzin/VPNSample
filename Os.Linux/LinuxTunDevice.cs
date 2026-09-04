using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VpnSample.Protocol;

namespace VpnSample.Os;

public sealed class LinuxTunDevice : IPacketEndpoint, IAsyncDisposable
{
    const uint TunSetIff = 0x400454ca;
    const short IffTun = 0x0001;
    const short IffNoPi = 0x1000;

    LinuxTunDevice(FileStream packetReader, FileStream packetWriter)
    {
        PacketReader = packetReader;
        PacketWriter = packetWriter;
    }

    public Stream PacketReader { get; }
    public Stream PacketWriter { get; }

    public static async Task<LinuxTunDevice> OpenAsync(LinuxTunOptions options)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("This learning sample requires Linux TUN support.");

        ValidateInterfaceName(options.Name);
        var handle = File.OpenHandle("/dev/net/tun", FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, FileOptions.Asynchronous);
        var request = new byte[40];
        Encoding.ASCII.GetBytes(options.Name).CopyTo(request, 0);
        BinaryPrimitives.WriteInt16LittleEndian(request.AsSpan(16), (short)(IffTun | IffNoPi));

        if (ioctl(handle, TunSetIff, request) < 0)
        {
            handle.Dispose();
            throw new IOException(
                $"Could not create {options.Name}; run as root (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            await ConfigureAsync(options);
            return CreateDevice(handle, options.Name);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ValueTask InterruptReadAsync() => PacketReader.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        await PacketReader.DisposeAsync();
        await PacketWriter.DisposeAsync();
    }

    static async Task ConfigureAsync(LinuxTunOptions options)
    {
        await RunAsync("ip", "-4", "addr", "replace", options.Ipv4Address,
            "dev", options.Name);
        await RunAsync("ip", "link", "set", "dev", options.Name,
            "addrgenmode", "none");
        await RunAsync("ip", "-6", "addr", "replace", options.Ipv6Address,
            "nodad", "dev", options.Name);
        if (options.Mtu is not null)
            await RunAsync("ip", "link", "set", "dev", options.Name,
                "mtu", options.Mtu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await RunAsync("ip", "link", "set", options.Name, "up");
    }

    static LinuxTunDevice CreateDevice(SafeFileHandle readWriteHandle, string name)
    {
        var writeFileDescriptor = dup(readWriteHandle);
        if (writeFileDescriptor < 0)
            throw new IOException(
                $"Could not duplicate {name} handle (errno {Marshal.GetLastPInvokeError()}).");

        var writeHandle = new SafeFileHandle((IntPtr)writeFileDescriptor, true);
        try
        {
            var reader = new FileStream(readWriteHandle, FileAccess.Read, 1, true);
            try
            {
                var writer = new FileStream(writeHandle, FileAccess.Write, 1, false);
                return new LinuxTunDevice(reader, writer);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }
        catch
        {
            writeHandle.Dispose();
            throw;
        }
    }

    static void ValidateInterfaceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Encoding.ASCII.GetByteCount(name) > 15)
            throw new ArgumentException("A Linux interface name must contain 1-15 ASCII bytes.", nameof(name));
    }

    static async Task RunAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException($"{file} failed: {error.Trim()}");
    }

    [DllImport("libc", SetLastError = true)]
    static extern int ioctl(SafeFileHandle fd, uint request, byte[] data);

    [DllImport("libc", SetLastError = true)]
    static extern int dup(SafeFileHandle oldfd);
}

public sealed record LinuxTunOptions(
    string Name,
    string Ipv4Address,
    string Ipv6Address,
    int? Mtu = null);
