using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class TunnelHandshakeTests
{
    [Fact]
    public async Task AcceptsMatchingProfileAndWritesLocalHello()
    {
        byte[] peerHello = TunnelHandshake.EncodeHello("baseline");
        await using var transport = new ScriptedDuplexStream(peerHello);

        await TunnelHandshake.NegotiateAsync(
            transport,
            "baseline",
            CancellationToken.None);

        Assert.Equal(peerHello, transport.WrittenBytes);
    }

    [Fact]
    public async Task RejectsMismatchedProfile()
    {
        byte[] peerHello = TunnelHandshake.EncodeHello("different-profile");
        await using var transport = new ScriptedDuplexStream(peerHello);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TunnelHandshake.NegotiateAsync(
                transport,
                "baseline",
                CancellationToken.None));

        Assert.Contains("profile mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class ScriptedDuplexStream(byte[] incomingBytes) : Stream
    {
        readonly MemoryStream incoming = new(incomingBytes, writable: false);
        readonly MemoryStream outgoing = new();

        public byte[] WrittenBytes => outgoing.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            incoming.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            outgoing.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await incoming.DisposeAsync();
            await outgoing.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
