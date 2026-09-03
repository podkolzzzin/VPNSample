using System.Buffers;
using System.Runtime.CompilerServices;

namespace VpnSample.Protocol;

public sealed class TunnelPipeline : IAsyncDisposable
{
    readonly string profileName;
    readonly IWireCodec codec;
    readonly IReadOnlyList<ITunnelStage> stages;
    int hasRun;
    int isDisposed;

    internal TunnelPipeline(
        string profileName,
        IWireCodec codec,
        IReadOnlyList<ITunnelStage> stages)
    {
        this.profileName = profileName;
        this.codec = codec;
        this.stages = stages;
    }

    public string ProfileName => profileName;

    public async Task RunAsync(
        IPacketEndpoint packets,
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed != 0, this);
        if (Interlocked.Exchange(ref hasRun, 1) != 0)
            throw new InvalidOperationException("A tunnel pipeline can run only one session.");
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(transport);

        await TunnelHandshake.NegotiateAsync(transport, profileName, cancellationToken);

        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task send = SendPacketsAsync(packets.PacketReader, transport, sessionStop.Token);
        Task receive = ReceivePacketsAsync(transport, packets.PacketWriter, sessionStop.Token);
        Task completed = await Task.WhenAny(send, receive);
        sessionStop.Cancel();

        if (completed == receive)
            await packets.InterruptReadAsync();

        try
        {
            await Task.WhenAll(send, receive);
        }
        catch (OperationCanceledException) when (completed.IsCompletedSuccessfully)
        {
            // The unfinished direction was canceled after its peer completed normally.
        }

        await completed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) != 0)
            return;

        for (int index = stages.Count - 1; index >= 0; index--)
            await DisposeComponentAsync(stages[index]);

        await DisposeComponentAsync(codec);
    }

    async Task SendPacketsAsync(
        Stream packetReader,
        Stream transport,
        CancellationToken cancellationToken)
    {
        IAsyncEnumerable<TunnelFrame> frames = ReadPacketsAsync(packetReader, cancellationToken);
        frames = ApplyOutboundStages(frames, cancellationToken);

        await foreach (TunnelFrame frame in frames.WithCancellation(cancellationToken))
            await codec.WriteAsync(transport, frame, cancellationToken);
    }

    async Task ReceivePacketsAsync(
        Stream transport,
        Stream packetWriter,
        CancellationToken cancellationToken)
    {
        IAsyncEnumerable<TunnelFrame> frames = ReadFramesAsync(transport, cancellationToken);
        frames = ApplyInboundStages(frames, cancellationToken);

        await foreach (TunnelFrame frame in frames.WithCancellation(cancellationToken))
        {
            if (!frame.IsCompletePacket)
                throw new InvalidDataException(
                    $"Pipeline output contains incomplete packet {frame.PacketId}, " +
                    $"fragment {frame.FragmentIndex + 1}/{frame.FragmentCount}.");

            await packetWriter.WriteAsync(frame.Payload, cancellationToken);
        }
    }

    static async IAsyncEnumerable<TunnelFrame> ReadPacketsAsync(
        Stream packetReader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ulong packetId = 0;
        byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(TunnelFrame.MaximumPayloadLength);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int length = await packetReader.ReadAsync(
                    packetBuffer.AsMemory(0, TunnelFrame.MaximumPayloadLength),
                    cancellationToken);
                if (length == 0)
                    throw new EndOfStreamException("The packet endpoint closed.");

                if (++packetId == 0)
                    packetId = 1;

                // Stages may buffer frames, so each emitted packet needs independent storage.
                yield return TunnelFrame.FromPacket(
                    packetId,
                    packetBuffer.AsMemory(0, length).ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packetBuffer);
        }
    }

    async IAsyncEnumerable<TunnelFrame> ReadFramesAsync(
        Stream transport,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
            yield return await codec.ReadAsync(transport, cancellationToken);
    }

    internal IAsyncEnumerable<TunnelFrame> ApplyOutboundStages(
        IAsyncEnumerable<TunnelFrame> frames,
        CancellationToken cancellationToken)
    {
        foreach (ITunnelStage stage in stages)
            frames = stage.OutboundAsync(frames, cancellationToken);
        return frames;
    }

    internal IAsyncEnumerable<TunnelFrame> ApplyInboundStages(
        IAsyncEnumerable<TunnelFrame> frames,
        CancellationToken cancellationToken)
    {
        for (int index = stages.Count - 1; index >= 0; index--)
            frames = stages[index].InboundAsync(frames, cancellationToken);
        return frames;
    }

    static async ValueTask DisposeComponentAsync(object component)
    {
        switch (component)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
