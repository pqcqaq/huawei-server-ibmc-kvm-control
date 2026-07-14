using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class KvmCipherSuiteParserTests
{
    [Fact]
    public void ParsesAndPrefersSha256Offer()
    {
        var payload = Convert.FromHexString("43010202000013880300002710");

        var suites = KvmCipherSuiteParser.Parse(payload);

        Assert.Equal(2, suites.Count);
        Assert.Equal(new KvmCipherSuite(2, 5000), suites[0]);
        Assert.Equal(new KvmCipherSuite(3, 10000), KvmCipherSuiteParser.SelectPreferred(suites));
    }

    [Fact]
    public void FallsBackToLegacySuite()
    {
        Assert.Equal(
            new KvmCipherSuite(1, 5000),
            KvmCipherSuiteParser.SelectPreferred(Array.Empty<KvmCipherSuite>()));
    }

    [Fact]
    public void RejectsMismatchedCount()
    {
        Assert.Throws<InvalidDataException>(() =>
            KvmCipherSuiteParser.Parse(Convert.FromHexString("4301020200001388")));
    }
}
