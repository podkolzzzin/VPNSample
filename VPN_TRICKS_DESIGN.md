# Extensible Design for VPN Demonstrations

Use a **layered Decorator pipeline**, configured with the **Strategy pattern**.

The core architecture separates orchestration, framing, tracing, and transport
encoding so subsequent tricks can be added as plug-ins.

## Current implementation status

The extensible foundation and the first transformation profile are implemented:

- `TunnelPipeline` owns the bidirectional pumps.
- `ITunnelStage` defines outbound and inbound decorators.
- `TunnelPipelineBuilder` makes stage order explicit.
- `TunnelFrame` carries packet and fragment metadata.
- `IWireCodec` separates stream encoding from frame transformations.
- `LengthPrefixedCodec` implements the baseline TCP wire format.
- `TunnelHandshake` rejects protocol-version or profile mismatches.
- `HttpsTunnelTransport` wraps the wire stream in TLS 1.2/1.3 and performs a
  valid HTTP/1.1 Upgrade on `/vpn` before the VPN handshake.
- `TunnelProfileFactory.Create` selects a named profile in one place; its
  `baseline` case composes tracing and pass-through stages.
- `PacketShuffleStage` buffers up to three whole packets for at most 5 ms and
  deliberately emits them in another order.
- `FragmentStage` splits packets into 256-byte tunnel frames and validates and
  reassembles them on receipt.
- `shuffle-split` composes tracing, packet shuffling, and fragmentation while
  leaving `baseline` available for comparison.

Delays beyond the bounded shuffle window, dropping, padding, aggregation,
alternative masking, and routing policies remain planned.

```text
TUN
 |
 v
Packet pipeline          filtering, delaying, reordering
 |
 v
Tunnel-frame pipeline    fragmentation, padding, aggregation
 |
 v
Wire codec               plain framing or HTTP-like encoding
 |
v
HTTPS transport (TLS + HTTP Upgrade on TCP/443)
```

## Tunnel stages

A useful core abstraction is:

```csharp
public interface ITunnelStage
{
    IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken);

    IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken);
}
```

`IAsyncEnumerable` is important because a stage may:

- Produce several frames from one packet, as with splitting.
- Produce no frame yet, as when buffering for reordering.
- Combine several frames into one.
- Delay frames.

The selected tricks can then be composed explicitly:

```csharp
await using var pipeline = new TunnelPipelineBuilder(
        "fragment-demo",
        new LengthPrefixedCodec())
    .Use(new PacketTraceStage("client"))
    .Use(new PacketShuffleStage(windowSize: 3, TimeSpan.FromMilliseconds(5)))
    .Use(new FragmentStage(maximumFragmentLength: 256))
    .Build();

await pipeline.RunAsync(tun, transport);
```

Each new demonstration becomes one class plus one registration line.

## Put each feature at the correct layer

| Trick | Best abstraction |
|---|---|
| Packet delay, dropping, or reordering | `ITunnelStage` |
| Tunnel-frame splitting and reassembly | `ITunnelStage` with packet IDs and fragment indexes |
| Padding or aggregation | `ITunnelStage` |
| HTTPS wrapping | `HttpsTunnelTransport` (implemented) |
| Alternative HTTP-like masking | `IWireCodec` or a transport decorator |
| TCP versus UDP transport | `ITunnelTransport` Strategy |
| Split tunneling | `IRoutingPolicy`, outside the protocol pipeline |

Split tunneling is different from the other tricks: it decides which packets
enter `svpn0` through Linux routes. The current all-or-nothing choice lives in
`scripts/run-vpn.sh`. Model routing separately:

```csharp
public interface IRoutingPolicy
{
    Task ApplyAsync(CancellationToken cancellationToken);
    Task RestoreAsync(CancellationToken cancellationToken);
}
```

Example strategies could be `FullTunnelPolicy`, `PeerOnlyPolicy`, and
`SelectedNetworksPolicy`.

## Suggested project layout

```text
Protocol/
  TunnelPipeline.cs
  TunnelPipelineBuilder.cs
  TunnelFrame.cs
  ITunnelStage.cs
  IWireCodec.cs
  TunnelHandshake.cs
  Stages/
    PacketTraceStage.cs
    PassThroughStage.cs
    FragmentStage.cs
    PacketShuffleStage.cs
    # Future: DelayStage.cs, PaddingStage.cs
  Codecs/
    LengthPrefixedCodec.cs
    # Future: HttpLikeCodec.cs
  Profiles/
    TunnelProfileFactory.cs

Os.Linux/
  Routing/
    # Future: FullTunnelPolicy.cs, SelectedNetworksPolicy.cs
```

## Important protocol details

1. Every tunnel packet receives a packet ID before the stage chain, together
   with a fragment index and fragment count in its wire envelope. Splitting and
   reassembly preserve and validate these fields.
2. The client/server handshake carries the protocol version and selected
   profile. Mismatched profiles are rejected before frame exchange.
3. Make stage order visible in the profile because stages may not commute. The
   implemented profile reorders complete IP packets before fragmenting them.
4. Keep the implemented pass-through profile so every experiment can be
   compared with the current baseline.

Splitting TCP `WriteAsync` calls is not meaningful packet splitting. TCP may
merge or divide writes arbitrarily. Split explicit tunnel frames, or use UDP if
the demonstration is intended to show real transport-level reordering.

The current transformations perturb the encrypted inner traffic but are not an
anti-DPI guarantee. TLS metadata, the endpoint, traffic volume, record sizes,
and timing remain observable.

## Recommended pattern summary

Use **Decorator/Chain of Responsibility** for the ordered tunnel stages, and
use **Strategy** for codecs, transports, demonstration profiles, and routing
policies. This keeps the pipeline readable during demonstrations and minimizes
the changes needed for each new trick.
