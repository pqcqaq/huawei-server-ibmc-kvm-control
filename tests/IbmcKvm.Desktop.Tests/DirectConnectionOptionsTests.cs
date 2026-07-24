namespace IbmcKvm.Desktop.Tests;

public sealed class DirectConnectionOptionsTests
{
    [Fact]
    public void MissingDirectOptionReturnsNull()
    {
        Assert.Null(DirectConnectionOptions.Parse([]));
    }

    [Fact]
    public void ParsesHostPortAndCodeKey()
    {
        var options = DirectConnectionOptions.Parse(["--direct-kvm=127.0.0.1:4100", "--code-key=99"]);

        Assert.Equal(new DirectConnectionOptions("127.0.0.1", 4100, 99), options);
    }

    [Theory]
    [InlineData("--direct-kvm=missing-port")]
    [InlineData("--direct-kvm=host:0")]
    [InlineData("--direct-kvm=host:70000")]
    public void RejectsInvalidEndpoint(string argument)
    {
        Assert.Throws<ArgumentException>(() => DirectConnectionOptions.Parse([argument]));
    }

    [Fact]
    public void ParsesAgentEndpointTokenFileAndFingerprint()
    {
        var tokenFile = Path.GetFullPath("pairing-token");
        const string fingerprint = "00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:" +
                                   "10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F";

        var options = DirectAgentConnectionOptions.Parse([
            "--direct-agent=127.0.0.1:7443",
            $"--agent-token-file={tokenFile}",
            $"--agent-fingerprint={fingerprint}",
        ]);

        Assert.Equal(
            new DirectAgentConnectionOptions(
                "127.0.0.1",
                7443,
                tokenFile,
                fingerprint.Replace(":", string.Empty, StringComparison.Ordinal)),
            options);
    }

    [Theory]
    [InlineData("--direct-agent=127.0.0.1:7443")]
    [InlineData("--agent-token-file=pairing-token")]
        [InlineData("--agent-fingerprint=AA:BB:CC")]
    public void RejectsIncompleteAgentOptions(string argument)
    {
        Assert.Throws<ArgumentException>(() => DirectAgentConnectionOptions.Parse([argument]));
    }

    [Fact]
    public void RejectsInvalidAgentCertificateFingerprint()
    {
        Assert.Throws<FormatException>(() => DirectAgentConnectionOptions.Parse([
            "--direct-agent=127.0.0.1:7443",
            "--agent-token-file=pairing-token",
            "--agent-fingerprint=AA:BB:CC",
        ]));
    }

    [Fact]
    public async Task ReadsPairingTokenFromFileAndTrimsLineEnding()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "cGFpcmluZy10b2tlbg==\r\n");
            RestrictTokenFile(path);
            var options = new DirectAgentConnectionOptions("localhost", 7443, path, "AA");

            Assert.Equal("cGFpcmluZy10b2tlbg==", await options.ReadPairingTokenAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsEmptyPairingTokenFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            RestrictTokenFile(path);
            var options = new DirectAgentConnectionOptions("localhost", 7443, path, "AA");

            await Assert.ThrowsAsync<InvalidDataException>(() => options.ReadPairingTokenAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsTokenFileReadableByOtherUnixUsers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "cGFpcmluZy10b2tlbg==");
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var options = new DirectAgentConnectionOptions("localhost", 7443, path, "AA");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => options.ReadPairingTokenAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RestrictTokenFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
