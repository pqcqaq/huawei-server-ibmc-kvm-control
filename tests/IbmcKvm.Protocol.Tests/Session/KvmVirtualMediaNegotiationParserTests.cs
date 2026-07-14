using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class KvmVirtualMediaNegotiationParserTests
{
    [Fact]
    public void ParsesCredentialSaltAndLittleEndianPort()
    {
        var payload = new byte[38];
        payload[0] = 0x32;
        payload[1] = 7;
        for (var index = 0; index < 20; index++)
        {
            payload[index + 2] = (byte)index;
        }

        for (var index = 0; index < 16; index++)
        {
            payload[index + 22] = (byte)(0xA0 + index);
        }

        var credential = KvmVirtualMediaNegotiationParser.ParseCredential(payload);
        var port = KvmVirtualMediaNegotiationParser.ParsePort([0x36, 7, 0x34, 0x12]);

        Assert.Equal(7, credential.BladeNumber);
        Assert.Equal(Enumerable.Range(0, 20).Select(static value => (byte)value), credential.Credential);
        Assert.Equal(Enumerable.Range(0, 16).Select(static value => (byte)(0xA0 + value)), credential.Salt);
        Assert.Equal((byte)7, port.BladeNumber);
        Assert.Equal(0x1234, port.Port);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ParsesPrivilegeStates(byte state, bool denied)
    {
        var result = KvmVirtualMediaNegotiationParser.ParsePrivilege([0x51, 0, state]);

        Assert.Equal(state, result.State);
        Assert.Equal(denied, KvmVirtualMediaNegotiationParser.IsDeniedPrivilege(result.State));
    }

    [Theory]
    [InlineData("32")]
    [InlineData("3200")]
    [InlineData("32000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void RejectsMalformedCredentialResponses(string hex)
    {
        Assert.Throws<InvalidDataException>(() =>
            KvmVirtualMediaNegotiationParser.ParseCredential(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("360000")]
    [InlineData("36000000")]
    [InlineData("35003412")]
    public void RejectsMalformedPorts(string hex)
    {
        Assert.Throws<InvalidDataException>(() =>
            KvmVirtualMediaNegotiationParser.ParsePort(Convert.FromHexString(hex)));
    }
}
