using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IbmcKvm.Core.Session;
using IbmcKvm.Core.VirtualMedia;
using IbmcKvm.Core.VirtualMedia.Scsi;
using IbmcKvm.Core.Tests.VirtualMedia.Scsi;
using IbmcKvm.Protocol.Session;
using IbmcKvm.Protocol.VirtualMedia;

namespace IbmcKvm.Core.Tests.VirtualMedia;

public sealed class VirtualMediaSessionTests
{
    [Fact]
    public async Task AuthenticatesCreatesTwoDevicesDispatchesInquiryAndWrites()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var credential = "01234567890123456789"u8.ToArray();
        var salt = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
        var endpoint = new KvmVirtualMediaEndpoint(
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port,
            credential,
            salt,
            new KvmCipherSuite(1, 5000));
        var server = RunServerAsync(listener, credential, salt, cancellation.Token);
        var imagePath = Path.Combine(Path.GetTempPath(), $"ibmc-vmm-{Guid.NewGuid():N}.img");
        await File.WriteAllBytesAsync(imagePath, new byte[4096], cancellation.Token);

        try
        {
            await using var image = new FileImageMedia(imagePath, MediaDeviceKind.Floppy, isReadOnly: false);
            await using var session = await VirtualMediaSession.ConnectAsync(endpoint, cancellation.Token);
            await session.MountAsync(image, cancellation.Token);
            await session.MountAsync(new ScsiTestMedia(MediaDeviceKind.Optical, 2048, 8), cancellation.Token);
            Assert.Contains(MediaDeviceKind.Floppy, session.MountedDevices);
            Assert.Contains(MediaDeviceKind.Optical, session.MountedDevices);
            try
            {
                await server.WaitForInquiryAndWriteAsync(session, cancellation.Token);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"VMM session failure: {session.Failure?.ToString() ?? "none"}", exception);
            }

            await session.EjectAsync(MediaDeviceKind.Floppy, cancellation.Token);
            Assert.DoesNotContain(MediaDeviceKind.Floppy, session.MountedDevices);
            await server.WaitForCloseAsync(cancellation.Token);
        }
        finally
        {
            File.Delete(imagePath);
            listener.Stop();
        }

        await server.Task;
    }

    [Fact]
    public async Task RejectsDeviceWhenServerReturnsEnumerationFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = new KvmVirtualMediaEndpoint(
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port,
            "01234567890123456789"u8,
            new byte[16],
            new KvmCipherSuite(1, 5000));
        var server = RunRejectingServerAsync(listener, cancellation.Token);

        await using var session = await VirtualMediaSession.ConnectAsync(endpoint, cancellation.Token);
        var media = new ScsiTestMedia(MediaDeviceKind.Floppy, 512, 8);
        await Assert.ThrowsAsync<IOException>(() => session.MountAsync(media, cancellation.Token));
        Assert.Empty(session.MountedDevices);
        await server;
    }

    private static async Task RunRejectingServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var codec = new VmmPacketCodec();
        await codec.ReadAsync(stream, cancellationToken);
        await SendAsync(stream, codec, new VmmPacket(VmmPacketType.Acknowledgement, 0, 0, 0, []), cancellationToken);
        await codec.ReadAsync(stream, cancellationToken);
        await SendAsync(stream, codec, new VmmPacket(VmmPacketType.Acknowledgement, 0, 0x11, 0, []), cancellationToken);
    }

    private static async Task SendAsync(
        NetworkStream stream,
        VmmPacketCodec codec,
        VmmPacket packet,
        CancellationToken cancellationToken)
    {
        var bytes = codec.Encode(packet);
        for (var offset = 0; offset < bytes.Length; offset += 3)
        {
            var count = Math.Min(3, bytes.Length - offset);
            await stream.WriteAsync(bytes.AsMemory(offset, count), cancellationToken);
        }
    }

    private sealed class ServerTask(Task task, Task operations, Task close)
    {
        public Task Task { get; } = task;
        public async Task WaitForInquiryAndWriteAsync(VirtualMediaSession _, CancellationToken cancellationToken) =>
            await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        public async Task WaitForCloseAsync(CancellationToken cancellationToken) =>
            await close.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServerTask RunServerAsync(TcpListener listener, byte[] credential, byte[] salt, CancellationToken cancellationToken)
    {
        // The handshake must happen before the background command script is exposed.
        var operationsSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var stream = client.GetStream();
                var codec = new VmmPacketCodec();
                var derived = VmmCredentialDeriver.Derive(credential, salt, new KvmCipherSuite(1, 5000));
                Assert.Equal(VmmPacketType.Authenticate, (await codec.ReadAsync(stream, cancellationToken)).Type);
                await SendAsync(stream, codec, new VmmPacket(VmmPacketType.Acknowledgement, 0, 0, 0, []), cancellationToken);
                var create = await codec.ReadAsync(stream, cancellationToken);
                Assert.Equal(VmmDeviceType.Floppy, (VmmDeviceType)create.Field1);
                await SendAsync(stream, codec, new VmmPacket(VmmPacketType.Acknowledgement, 0, 0x10, 0, []), cancellationToken);
                var createOptical = await codec.ReadAsync(stream, cancellationToken);
                Assert.Equal(VmmDeviceType.Optical, (VmmDeviceType)createOptical.Field1);
                await SendAsync(stream, codec, new VmmPacket(VmmPacketType.Acknowledgement, 0, 0x10, 0, []), cancellationToken);
                var inquiry = VmmPacket.Data(VmmDeviceType.Floppy, VmmTransferKind.Command, VmmTransferState.End, 7, Command(0x12));
                await SendAsync(stream, codec, inquiry, cancellationToken);
                var inquiryData = await codec.ReadAsync(stream, cancellationToken);
                Assert.Equal(VmmPacketType.FloppyData, inquiryData.Type);
                Assert.Equal((byte)'V', VmmCredentialDeriver.UnwrapEncryptedPayload(inquiryData.Payload, derived)[8]);
                Assert.Equal(VmmPacketType.FloppyCommandComplete, (await codec.ReadAsync(stream, cancellationToken)).Type);
                await SendAsync(stream, codec, VmmPacket.Data(VmmDeviceType.Floppy, VmmTransferKind.Command, VmmTransferState.End, 8, Command(0x00)), cancellationToken);
                Assert.Equal(1, (await codec.ReadAsync(stream, cancellationToken)).Field1);
                await SendAsync(stream, codec, VmmPacket.Data(VmmDeviceType.Floppy, VmmTransferKind.Command, VmmTransferState.End, 9, Command(0x00)), cancellationToken);
                Assert.Equal(0, (await codec.ReadAsync(stream, cancellationToken)).Field1);
                await SendAsync(stream, codec, VmmPacket.Data(VmmDeviceType.Floppy, VmmTransferKind.Command, VmmTransferState.End, 10, BlockCommand(0x2A, 1, 1)), cancellationToken);
                var writeData = Enumerable.Repeat((byte)0x5A, 512).ToArray();
                await SendAsync(stream, codec, VmmPacket.Data(VmmDeviceType.Floppy, VmmTransferKind.Data, VmmTransferState.End, 10, VmmCredentialDeriver.WrapEncryptedPayload(writeData, derived)), cancellationToken);
                var complete = await codec.ReadAsync(stream, cancellationToken);
                Assert.Equal(0, complete.Field1);
                operationsSource.TrySetResult();
                var close = await codec.ReadAsync(stream, cancellationToken);
                Assert.Equal(VmmPacketType.Close, close.Type);
                closeSource.TrySetResult();
            }
            catch (Exception exception)
            {
                operationsSource.TrySetException(exception);
                closeSource.TrySetException(exception);
                throw;
            }
        }, cancellationToken);
        return new ServerTask(task, operationsSource.Task, closeSource.Task);
    }

    private static byte[] Command(byte opcode)
    {
        var result = new byte[12];
        result[0] = opcode;
        return result;
    }

    private static byte[] BlockCommand(byte opcode, uint lba, uint blocks)
    {
        var result = Command(opcode);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(2, 4), lba);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(7, 2), checked((ushort)blocks));
        return result;
    }
}
