namespace VpnSample.Protocol;

public static class TunnelProfileFactory
{
    public const string BaselineProfileName = "baseline";
    public const string ShuffleSplitProfileName = "shuffle-split";
    public const string WebSocketCoverProfileName = "websocket-cover";
    public const string DefaultProfileName = WebSocketCoverProfileName;

    public static bool IsSupported(string profileName) =>
        profileName is BaselineProfileName or ShuffleSplitProfileName or WebSocketCoverProfileName;

    public static TunnelPipeline Create(string profileName, string traceSide) =>
        profileName switch
        {
            BaselineProfileName => CreateBaseline(traceSide),
            ShuffleSplitProfileName => CreateShuffleSplit(traceSide),
            WebSocketCoverProfileName => CreateWebSocketCover(traceSide),
            _ => throw new ArgumentException($"Unknown tunnel profile: '{profileName}'.", nameof(profileName))
        };

    public static TunnelPipeline CreateBaseline(string traceSide) =>
        new TunnelPipelineBuilder(BaselineProfileName, new LengthPrefixedCodec())
            .Use(new PacketTraceStage(traceSide))
            .Use(new PassThroughStage())
            .Build();

    public static TunnelPipeline CreateShuffleSplit(string traceSide) =>
        new TunnelPipelineBuilder(ShuffleSplitProfileName, new LengthPrefixedCodec())
            .Use(new PacketTraceStage(traceSide))
            .Use(new PacketShuffleStage(windowSize: 3, TimeSpan.FromMilliseconds(5)))
            .Use(new FragmentStage(maximumFragmentLength: 256))
            .Build();

    public static TunnelPipeline CreateWebSocketCover(string traceSide) =>
        new TunnelPipelineBuilder(WebSocketCoverProfileName, new LengthPrefixedCodec())
            .Use(new PacketTraceStage(traceSide))
            .Use(new PacketShuffleStage(windowSize: 3, TimeSpan.FromMilliseconds(5)))
            .Use(new FragmentStage(maximumFragmentLength: 240))
            .Use(new PaddingStage(64, 128, 256, 512, 1024, 1440))
            .Build();
}
