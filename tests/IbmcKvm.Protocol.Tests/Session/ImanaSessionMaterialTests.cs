using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class ImanaSessionMaterialTests
{
    [Theory]
    [InlineData(0, "924CF832EA2733EC969443E31CEB90B327BFBAD9296ECB83")]
    [InlineData(7, "D1484EA544E9DA91F5326203BDE774B02361DBDAE9A85F35")]
    public void DerivesTheSourceProtocolSessionId(int codeKey, string expectedHex)
    {
        // V1 uses PBKDF2-HMAC-SHA1, 5000 iterations, a zero salt, and retains
        // the first 24 bytes of its 72-byte key expansion as the session ID.
        Assert.Equal(Convert.FromHexString(expectedHex), ImanaSessionMaterial.DeriveSessionId(codeKey));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void RejectsNonDigitCodeKeys(int codeKey)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImanaSessionMaterial.DeriveSessionId(codeKey));
    }

    [Fact]
    public void MatchesOriginalV1InputAndVideoTransforms()
    {
        using var cryptography = ImanaSessionCryptography.FromCodeKey(7);

        Assert.Equal(
            Convert.FromHexString("D1484EA544E9DA91F5326203BDE774B02361DBDAE9A85F35"),
            cryptography.SessionId.ToArray());
        Assert.Equal(
            Convert.FromHexString("98CE1197C011EAF8088BB24D6A784B74"),
            cryptography.EncryptInput(Convert.FromHexString("05004C0000000000")));
        Assert.Equal(
            Convert.FromHexString("0102030405060708090A0B0C0D"),
            cryptography.DecryptData(
                Convert.FromHexString("A1B84B9632F900CCD66A52A4A771DB1D"),
                13));
    }
}
