namespace VpnSample.Protocol;

public static class TunnelProfileFactory
{
    public const string BaselineProfileName = "baseline";
    public const string ShuffleSplitProfileName = "shuffle-split";
    public const string DefaultProfileName = ShuffleSplitProfileName;

    public static bool IsSupported(string profileName) =>
        profileName is BaselineProfileName or ShuffleSplitProfileName;

    public static TunnelPipeline Create(string profileName, string traceSide) =>
        profileName switch
        {
            BaselineProfileName => CreateBaseline(traceSide),
            ShuffleSplitProfileName => CreateShuffleSplit(traceSide),
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
}
