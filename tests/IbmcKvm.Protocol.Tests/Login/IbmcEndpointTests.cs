using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class IbmcEndpointTests
{
    [Theory]
    [InlineData("192.0.2.10", "192.0.2.10", 443)]
    [InlineData("192.0.2.10:8443", "192.0.2.10", 8443)]
    [InlineData("ibmc.example.test", "ibmc.example.test", 443)]
    [InlineData("[2001:db8::10]", "2001:db8::10", 443)]
    [InlineData("[2001:db8::10]:9443", "2001:db8::10", 9443)]
    [InlineData("2001:db8::10", "2001:db8::10", 443)]
    public void ParseAcceptsSupportedAddressForms(string input, string expectedHost, int expectedPort)
    {
        var endpoint = IbmcEndpoint.Parse(input);

        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.HttpsPort);
        Assert.Equal(623, endpoint.IpmiPort);
        Assert.Equal("/bmc/php/processparameter.php", endpoint.LoginUri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("[2001:db8::1")]
    [InlineData("host:not-a-port")]
    public void ParseRejectsInvalidAddressForms(string input)
    {
        Assert.Throws<FormatException>(() => IbmcEndpoint.Parse(input));
    }
}

