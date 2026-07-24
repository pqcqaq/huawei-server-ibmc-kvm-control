using System.Buffers.Binary;
using IbmcKvm.Core.Agent;

namespace IbmcKvm.Core.Tests.Agent;

public sealed class AgentProtocolTests
{
    [Fact]
    public void EncodesSharedPingVector()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, 0x0102030405060708);

        var bytes = AgentProtocol.Encode(new AgentEnvelope(AgentMessageKind.Ping, payload));

        Assert.Equal("494B414701070000000000080102030405060708", Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task ReadsFragmentedEnvelope()
    {
        var stream = new FragmentedReadStream(
            Convert.FromHexString("494B414701070000000000080102030405060708"),
            fragmentLength: 2);

        var envelope = await AgentProtocol.ReadAsync(stream);

        Assert.Equal(AgentMessageKind.Ping, envelope.Kind);
        Assert.Equal("0102030405060708", Convert.ToHexString(envelope.Payload));
    }

    [Fact]
    public async Task RejectsOversizedPayloadBeforeAllocation()
    {
        var header = Convert.FromHexString("494B41470103000001000001");

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await AgentProtocol.ReadAsync(new MemoryStream(header)));

        Assert.Contains("16777217", error.Message, StringComparison.Ordinal);
    }

    private sealed class FragmentedReadStream(byte[] bytes, int fragmentLength) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentLength)], cancellationToken);
    }
}
