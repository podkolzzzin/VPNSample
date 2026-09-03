using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace VpnSample.Protocol;

public sealed class PacketShuffleStage : ITunnelStage
{
    readonly int windowSize;
    readonly TimeSpan flushDelay;
    readonly Func<int, int> nextIndex;

    public PacketShuffleStage(int windowSize, TimeSpan flushDelay)
        : this(windowSize, flushDelay, RandomNumberGenerator.GetInt32)
    {
    }

    internal PacketShuffleStage(
        int windowSize,
        TimeSpan flushDelay,
        Func<int, int> nextIndex)
    {
        if (windowSize < 2)
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (flushDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(flushDelay));

        ArgumentNullException.ThrowIfNull(nextIndex);
        this.windowSize = windowSize;
        this.flushDelay = flushDelay;
        this.nextIndex = nextIndex;
    }

    public IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken) =>
        ShuffleAsync(input, cancellationToken);

    public async IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Deliberately preserve the sender's shuffled order. Inner protocols,
        // such as TCP, are responsible for their own packet sequencing.
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            if (!frame.IsCompletePacket)
                throw new InvalidDataException("PacketShuffleStage expects reassembled inbound packets.");
            yield return frame;
        }
    }

    async IAsyncEnumerable<TunnelFrame> ShuffleAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var window = new List<TunnelFrame>(windowSize);
        await using IAsyncEnumerator<TunnelFrame> enumerator =
            input.GetAsyncEnumerator(cancellationToken);
        Task<bool>? pendingMove = null;

        while (true)
        {
            pendingMove ??= enumerator.MoveNextAsync().AsTask();
            bool hasFrame;

            if (window.Count == 0)
            {
                hasFrame = await pendingMove;
            }
            else
            {
                Task delay = Task.Delay(flushDelay, cancellationToken);
                Task completed = await Task.WhenAny(pendingMove, delay);
                if (completed == delay)
                {
                    await delay;
                    foreach (TunnelFrame bufferedFrame in Shuffle(window))
                        yield return bufferedFrame;
                    window.Clear();
                    continue;
                }

                hasFrame = await pendingMove;
            }

            pendingMove = null;
            if (!hasFrame)
            {
                foreach (TunnelFrame bufferedFrame in Shuffle(window))
                    yield return bufferedFrame;
                yield break;
            }

            TunnelFrame frame = enumerator.Current;
            if (!frame.IsCompletePacket)
                throw new InvalidDataException("PacketShuffleStage expects complete outbound packets.");
            window.Add(frame);

            if (window.Count < windowSize)
                continue;

            foreach (TunnelFrame bufferedFrame in Shuffle(window))
                yield return bufferedFrame;
            window.Clear();
        }
    }

    IEnumerable<TunnelFrame> Shuffle(List<TunnelFrame> window)
    {
        if (window.Count < 2)
            return window;

        ulong[] originalOrder = window.Select(frame => frame.PacketId).ToArray();
        for (int index = window.Count - 1; index > 0; index--)
        {
            int swapIndex = nextIndex(index + 1);
            if ((uint)swapIndex > (uint)index)
                throw new InvalidOperationException("The shuffle index generator returned an invalid index.");
            (window[index], window[swapIndex]) = (window[swapIndex], window[index]);
        }

        if (window.Select(frame => frame.PacketId).SequenceEqual(originalOrder))
            (window[0], window[1]) = (window[1], window[0]);

        return window;
    }
}
