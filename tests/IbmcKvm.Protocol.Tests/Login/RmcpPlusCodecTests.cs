using System.Buffers.Binary;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class RmcpPlusCodecTests
{
    [Fact]
    public void BuildsIpmiRequestWithBothChecksums()
    {
        var command = new RmcpOemCommand(0x30, 0x93, [0xDB, 0x07, 0x00]);

        var request = RmcpPlusCodec.BuildIpmiRequest(command, 3);

        Assert.Equal(
            Convert.FromHexString("20C020810C93DB0700FE"),
            request);
    }

    [Fact]
    public void BuildsAuthenticatedEncryptedPacketAndParsesResponse()
    {
        var command = new RmcpOemCommand(0x30, 0x94, [0xDB, 0x07, 0x00, 0x20, 0x02, 0x00, 0xFF]);
        var k1 = Enumerable.Range(1, 20).Select(static value => (byte)value).ToArray();
        var k2 = Enumerable.Range(21, 20).Select(static value => (byte)value).ToArray();
        var response = BuildResponse(command, [0x01, 0x00, 0x01], sequence: 4);

        var packet = RmcpPlusCodec.BuildSecureIpmiPacket(0x01020304, 9, response, k1, k2);
        var payload = RmcpPlusCodec.ParseSecureIpmiResponse(
            packet,
            0x01020304,
            command,
            k1,
            k2);

        Assert.Equal(new byte[] { 0x01, 0x00, 0x01 }, payload);
    }

    [Fact]
    public void OpenSessionRequestUsesRmcpPlusAlgorithmRecords()
    {
        var packet = RmcpPlusCodec.BuildOpenSessionRequest(7, 0x11223344);

        Assert.Equal(new byte[] { 0x06, 0x00, 0xFF, 0x07, 0x06, 0x10 }, packet[..6]);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)));
        Assert.Equal(RmcpPlusPayloadType.OpenSessionRequest, RmcpPlusCodec.GetPayloadType(packet));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RejectsUnsupportedOpenSessionStatus(byte status)
    {
        var packet = RmcpPlusCodec.BuildOpenSessionRequest(7, 1);
        var response = packet.ToArray();
        response[5] = (byte)RmcpPlusPayloadType.OpenSessionResponse;
        response[16 + 1] = status;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16 + 8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16 + 4), 1);

        if (status == 0)
        {
            // The request's algorithm payload is intentionally not a response fixture.
            Assert.Throws<InvalidDataException>(() => RmcpPlusCodec.ParseOpenSessionResponse(response, 7, 1));
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => RmcpPlusCodec.ParseOpenSessionResponse(response, 7, 1));
        }
    }

    private static byte[] BuildResponse(RmcpOemCommand command, byte[] data, byte sequence)
    {
        var response = new byte[8 + data.Length];
        response[0] = 0x20;
        response[1] = 0xC4;
        response[2] = Checksum(response.AsSpan(0, 2));
        response[3] = 0x81;
        response[4] = (byte)(sequence << 2);
        response[5] = command.Command;
        data.CopyTo(response.AsSpan(7));
        response[^1] = Checksum(response.AsSpan(3, response.Length - 4));
        return response;
    }

    private static byte Checksum(ReadOnlySpan<byte> value)
    {
        byte sum = 0;
        foreach (var item in value)
        {
            sum += item;
        }

        return unchecked((byte)-sum);
    }
}
