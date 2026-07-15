using IbmcKvm.Protocol.Login;
using IbmcKvm.Protocol.Profiles;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class RmcpOemLoginClientTests
{
    [Fact]
    public async Task DiscoversImanaAndKeepsPasswordOutOfOemPayloads()
    {
        var transport = new FakeTransport();
        var password = "secret".ToCharArray();
        try
        {
            var result = await new RmcpOemLoginClient(transport).LoginAsync(
                "192.0.2.10",
                623,
                "admin",
                password,
                ConnectionMode.Shared);

            Assert.IsType<ImanaKvmProtocolProfile>(result.Profile);
            Assert.True(result.KvmEncrypted);
            Assert.False(result.VirtualMediaEncrypted);
            Assert.Equal(0x1234, result.KvmPort);
            Assert.Equal(0x5678, result.VirtualMediaPort);
            Assert.Null(result.LoginDecryptionKey);
            Assert.Equal(6, transport.Commands.Count);
            Assert.All(transport.Commands, command =>
                Assert.Equal(-1, command.Data.Span.IndexOf("secret"u8)));
        }
        finally
        {
            password.AsSpan().Clear();
        }
    }

    private sealed class FakeTransport : IRmcpOemTransport
    {
        public List<RmcpOemCommand> Commands { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(
            string host,
            int port,
            string userName,
            ReadOnlyMemory<char> password,
            RmcpOemCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            ReadOnlyMemory<byte> response = command.Command switch
            {
                0x01 => DeviceId(),
                0x94 when command.Data.Span.Length >= 5 && command.Data.Span[3] == 0x20 =>
                    new byte[] { command.Data.Span[4] == 0x02 ? (byte)1 : (byte)0 },
                0x93 when command.Data.Span[6] == 0x01 => new byte[] { 0x34, 0x12 },
                0x93 when command.Data.Span[6] == 0x02 => new byte[] { 0x78, 0x56 },
                _ => Array.Empty<byte>(),
            };
            return ValueTask.FromResult(response);
        }

        private static byte[] DeviceId()
        {
            var response = new byte[14];
            response[13] = 0;
            return response;
        }
    }
}
