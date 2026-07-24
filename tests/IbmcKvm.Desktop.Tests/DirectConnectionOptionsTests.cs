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
}
