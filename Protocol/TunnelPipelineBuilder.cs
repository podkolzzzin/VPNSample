namespace VpnSample.Protocol;

public sealed class TunnelPipelineBuilder
{
    readonly string profileName;
    readonly IWireCodec codec;
    readonly List<ITunnelStage> stages = [];
    bool isBuilt;

    public TunnelPipelineBuilder(string profileName, IWireCodec codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(codec);

        this.profileName = profileName;
        this.codec = codec;
    }

    public TunnelPipelineBuilder Use(ITunnelStage stage)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(stage);
        stages.Add(stage);
        return this;
    }

    public TunnelPipeline Build()
    {
        ThrowIfBuilt();
        isBuilt = true;
        return new TunnelPipeline(profileName, codec, stages.ToArray());
    }

    void ThrowIfBuilt()
    {
        if (isBuilt)
            throw new InvalidOperationException("This tunnel pipeline builder has already built a pipeline.");
    }
}
