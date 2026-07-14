using IbmcKvm.Protocol.VirtualMedia;

namespace IbmcKvm.Protocol.Tests.VirtualMedia;

public sealed class VmmPacketCodecTests
{
    private readonly VmmPacketCodec codec = new();

    [Fact]
    public void MatchesJavaHeaderVectors()
    {
        var session = Enumerable.Range(0, 24).Select(static value => (byte)value).ToArray();

        Assert.Equal(
            "010000000000001D03010101000102030405060708090A0B0C0D0E0F101112131415161700C000020A",
            Convert.ToHexString(codec.Encode(VmmPacket.Authenticate(session, [192, 0, 2, 10]))));
        Assert.Equal("020100000000000000000000", Convert.ToHexString(codec.Encode(VmmPacket.CreateDevice(VmmDeviceType.Floppy))));
        Assert.Equal("020200000000000000000000", Convert.ToHexString(codec.Encode(VmmPacket.CreateDevice(VmmDeviceType.Optical))));
        Assert.Equal(
            "043100560000123400000000",
            Convert.ToHexString(codec.Encode(new VmmPacket(VmmPacketType.OpticalData, 0x31, 0, 0x56, new byte[0x1234]))[..12]));
        Assert.Equal(
            "033100560000123400000000",
            Convert.ToHexString(codec.Encode(new VmmPacket(VmmPacketType.FloppyData, 0x31, 0, 0x56, new byte[0x1234]))[..12]));
        Assert.Equal("FE0100560000000000000000", Convert.ToHexString(codec.Encode(VmmPacket.Complete(VmmDeviceType.Floppy, 1, 0x56))));
        Assert.Equal("FF0100560000000000000000", Convert.ToHexString(codec.Encode(VmmPacket.Complete(VmmDeviceType.Optical, 1, 0x56))));
        Assert.Equal("050200000000000000000000", Convert.ToHexString(codec.Encode(VmmPacket.Close(VmmDeviceType.Optical))));
    }

    [Fact]
    public async Task ReadsHeaderAndPayloadAcrossEveryFragmentBoundary()
    {
        var encoded = codec.Encode(VmmPacket.Data(
            VmmDeviceType.Optical,
            VmmTransferKind.Command,
            VmmTransferState.End,
            0x42,
            Enumerable.Range(0, 12).Select(static value => (byte)value).ToArray()));

        for (var fragmentSize = 1; fragmentSize <= encoded.Length; fragmentSize++)
        {
            await using var stream = new FragmentedReadStream(encoded, fragmentSize);
            var packet = await codec.ReadAsync(stream);

            Assert.Equal(VmmPacketType.OpticalData, packet.Type);
            Assert.Equal(0x30, packet.Field1);
            Assert.Equal(0x42, packet.CommandId);
            Assert.Equal(Enumerable.Range(0, 12).Select(static value => (byte)value), packet.Payload);
        }
    }

    [Fact]
    public async Task RejectsOversizedAndTruncatedFrames()
    {
        var bounded = new VmmPacketCodec(16);
        await using var oversized = new MemoryStream(Convert.FromHexString("040000000000001100000000"));
        await using var truncated = new MemoryStream(Convert.FromHexString("0400000000000004000000000102"));

        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.ReadAsync(oversized).AsTask());
        await Assert.ThrowsAsync<EndOfStreamException>(() => codec.ReadAsync(truncated).AsTask());
        Assert.Throws<InvalidDataException>(() => bounded.Encode(
            new VmmPacket(VmmPacketType.OpticalData, 0, 0, 0, new byte[17])));
    }

    [Fact]
    public async Task HonorsReadCancellation()
    {
        await using var stream = new NeverCompletingStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            codec.ReadAsync(stream, cancellation.Token).AsTask());
    }

    private sealed class FragmentedReadStream(byte[] data, int fragmentSize) : Stream
    {
        private int offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int bufferOffset, int count) =>
            ReadAsync(buffer.AsMemory(bufferOffset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset == data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(Math.Min(fragmentSize, buffer.Length), data.Length - offset);
            data.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
