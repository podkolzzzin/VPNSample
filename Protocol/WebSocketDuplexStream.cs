using System.Net.WebSockets;

namespace VpnSample.Protocol;

public sealed class WebSocketDuplexStream : Stream
{
    readonly WebSocket webSocket;
    readonly IDisposable? owner;
    bool isDisposed;

    public WebSocketDuplexStream(WebSocket webSocket, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        this.webSocket = webSocket;
        this.owner = owner;
    }

    public override bool CanRead => !isDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !isDisposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        while (true)
        {
            ValueWebSocketReceiveResult result =
                await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return 0;
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("The tunnel WebSocket accepts binary messages only.");
            if (result.Count != 0)
                return result.Count;
        }
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return webSocket.SendAsync(
            buffer,
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        if (isDisposed)
            return;
        isDisposed = true;

        try
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Tunnel closed",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // The peer may already have closed the underlying connection.
        }
        finally
        {
            webSocket.Dispose();
            owner?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || isDisposed)
            return;
        isDisposed = true;
        webSocket.Dispose();
        owner?.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
