using VpnSample.Dns;

namespace VpnSample.Dns.Tests;

public sealed class NodeRegistrationProtocolTests
{
    [Fact]
    public async Task ExchangesNormalizedNameAndClientNumber()
    {
        await using var request = new MemoryStream();
        await NodeRegistrationProtocol.WriteRequestAsync(request, "Nginx-Node");
        request.Position = 0;
        Assert.Equal("nginx-node", await NodeRegistrationProtocol.ReadRequestAsync(request));

        await using var response = new MemoryStream();
        await NodeRegistrationProtocol.WriteAcceptedAsync(response, 17);
        response.Position = 0;
        Assert.Equal(17, await NodeRegistrationProtocol.ReadResponseAsync(response));
    }

    [Fact]
    public async Task ReportsDuplicateName()
    {
        await using var response = new MemoryStream();
        await NodeRegistrationProtocol.WriteNameInUseAsync(response);
        response.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NodeRegistrationProtocol.ReadResponseAsync(response));
    }
}
