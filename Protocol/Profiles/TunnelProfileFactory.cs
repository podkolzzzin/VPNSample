namespace VpnSample.Protocol;

public static class TunnelProfileFactory
{
    public const string BaselineProfileName = "baseline";

    public static bool IsSupported(string profileName) =>
        profileName is BaselineProfileName;

    public static TunnelPipeline Create(string profileName, string traceSide) =>
        profileName switch
        {
            BaselineProfileName => CreateBaseline(traceSide),
            _ => throw new ArgumentException($"Unknown tunnel profile: '{profileName}'.", nameof(profileName))
        };

    public static TunnelPipeline CreateBaseline(string traceSide) =>
        new TunnelPipelineBuilder(BaselineProfileName, new LengthPrefixedCodec())
            .Use(new PacketTraceStage(traceSide))
            .Use(new PassThroughStage())
            .Build();
}
