using System.Buffers.Binary;
using IbmcKvm.Core.Agent;

namespace IbmcKvm.Core.Tests.Agent;

public sealed class AgentMessageCodecTests
{
    [Theory]
    [InlineData("host.example", "host.example", 7443)]
    [InlineData("192.0.2.5:8443", "192.0.2.5", 8443)]
    [InlineData("agent://[2001:db8::1]:7444", "[2001:db8::1]", 7444)]
    public void ParsesAgentEndpoint(string value, string expectedHost, int expectedPort)
    {
        var endpoint = AgentEndpoint.Parse(value);

        Assert.Equal(expectedHost.Trim('[', ']'), endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);
    }

    [Fact]
    public void ParsesServerHello()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), 1920);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), 1080);
        payload[4] = 10;
        payload[5] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), 7);

        var hello = AgentMessageCodec.ParseServerHello(new AgentEnvelope(AgentMessageKind.ServerHello, payload));

        Assert.Equal(1920, hello.Width);
        Assert.Equal(1080, hello.Height);
        Assert.Equal(AgentCapabilities.Keyboard | AgentCapabilities.Mouse | AgentCapabilities.AbsoluteMouse, hello.Capabilities);
    }

    [Fact]
    public void RejectsServerHelloWithExcessivePixelCount()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), 8193);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), 4096);
        payload[4] = 10;
        payload[5] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), 7);

        Assert.Throws<InvalidDataException>(() =>
            AgentMessageCodec.ParseServerHello(new AgentEnvelope(AgentMessageKind.ServerHello, payload)));
    }

    [Fact]
    public void RejectsKeyframeWithoutCompleteTileCoverage()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), 64);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), 64);
        payload[8] = 1;
        payload[9] = 64;

        var error = Assert.Throws<InvalidDataException>(() =>
            AgentMessageCodec.ParseFrame(new AgentEnvelope(AgentMessageKind.Frame, payload)));

        Assert.Contains("does not cover", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodesMouseReportInNetworkByteOrder()
    {
        var envelope = AgentMessageCodec.Mouse(new AgentMouseReport(3, 0x1234, 0x5678, -1));

        Assert.Equal(AgentMessageKind.Mouse, envelope.Kind);
        Assert.Equal("0312345678FF", Convert.ToHexString(envelope.Payload));
    }
}
