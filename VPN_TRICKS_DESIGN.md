# Extensible Design for VPN Demonstrations

Use a **layered Decorator pipeline**, configured with the **Strategy pattern**.

The existing `PacketTunnelProtocol` is the right place to make one initial
refactor. It currently combines orchestration, framing, tracing, and transport
writes in one class. Separate those concerns once, and subsequent tricks become
plug-ins.

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
TCP transport
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
var pipeline = new TunnelPipelineBuilder()
    .Use(new PacketTraceStage("client"))
    .Use(new FragmentStage(maxPayload: 400))
    .Use(new ReorderStage(windowSize: 3))
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
| HTTP-like masking | `IWireCodec` or a transport decorator |
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
  TunnelFrame.cs
  Stages/
    PassThroughStage.cs
    FragmentStage.cs
    ReorderStage.cs
    DelayStage.cs
  Codecs/
    LengthPrefixedCodec.cs
    HttpLikeCodec.cs
  Profiles/
    TunnelProfileFactory.cs

Os.Linux/
  Routing/
    FullTunnelPolicy.cs
    SelectedNetworksPolicy.cs
```

## Important protocol details

1. Give every tunnel packet a packet ID before splitting, together with a
   fragment index and fragment count. Otherwise splitting and reordering cannot
   be reversed reliably.
2. Add a small client/server handshake containing the protocol version and
   selected profile. Currently both sides assume identical framing; mismatched
   tricks would otherwise corrupt or hang the connection.
3. Make stage order visible in the profile because stages may not commute. For
   example, fragmenting before reordering demonstrates reordered fragments,
   while reordering before fragmenting demonstrates reordered IP packets.
4. Keep a pass-through profile so every experiment can be compared with the
   current baseline.

Splitting TCP `WriteAsync` calls is not meaningful packet splitting. TCP may
merge or divide writes arbitrarily. Split explicit tunnel frames, or use UDP if
the demonstration is intended to show real transport-level reordering.

## Recommended pattern summary

Use **Decorator/Chain of Responsibility** for the ordered tunnel stages, and
use **Strategy** for codecs, transports, demonstration profiles, and routing
policies. This keeps the pipeline readable during demonstrations and minimizes
the changes needed for each new trick.
:q
