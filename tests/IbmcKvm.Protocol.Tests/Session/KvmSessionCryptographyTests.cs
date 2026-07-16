using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class KvmSessionCryptographyTests
{
    private const string LoginKeyHex =
        "000102030405060708090A0B0C0D0E0F" +
        "101112131415161718191A1B1C1D1E1F";
    private const string MaterialCipherHex =
        "9BE37DD9A6322852855FD577ED0B73A0" +
        "AC624FE33E8DF0407F98AC07BF769C79" +
        "093417894576C00D9ECC936328DB2170";

    [Theory]
    [InlineData(2, 5000, "5678FDE91A8CBEC25BD92C9D5071C0D843721DBC9C843D57")]
    [InlineData(3, 10000, "0C829FAA0BD699D5A2A413E9BCA114061BE027FD07682FDC")]
    public void DerivesWordReversedConnectAuthenticatorMatchingKnownVector(
        byte algorithm,
        int iterations,
        string expectedHex)
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);

        var result = cryptography.DeriveConnectAuthenticator(
            "987654321",
            new KvmCipherSuite(algorithm, iterations));

        Assert.Equal(Convert.FromHexString(expectedHex), result);
    }

    [Fact]
    public void EstablishesSessionAndMatchesKnownTransforms()
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);
        cryptography.EstablishSession(
            Convert.FromHexString(MaterialCipherHex),
            new KvmCipherSuite(3, 10000));

        Assert.True(cryptography.IsSessionEstablished);
        Assert.Equal(
            Convert.FromHexString("1CE62A1C88E9C99EF265C6C1B3F44016"),
            cryptography.EncryptInput(Convert.FromHexString("05004C0000000000")));
        Assert.Equal(
            Convert.FromHexString("025685722DA2D7A823B424BAA23AA22F"),
            cryptography.EncryptInput(Convert.FromHexString("030BB805DCFF")));
        Assert.Equal(
            Convert.FromHexString("0102030405060708090A0B0C0D"),
            cryptography.DecryptData(Convert.FromHexString("47DE27D0AD3BBE09C20BD15981481452"), 13));
        Assert.Equal(
            Enumerable.Range(0x20, 36).Select(static value => (byte)value),
            cryptography.DecryptData(
                Convert.FromHexString(
                    "D95A2BC881C87396CC4D67F2EB189905" +
                    "90B17F9464FE2A49AF2B02293D2BF249" +
                    "CDEEC792D045FAFB453D9B671E6A2826"),
                36));
        Assert.Equal(
            Convert.FromHexString("8DA322A5E47D81800AA0FF60CB4E5E37"),
            cryptography.EncryptPower(KvmPowerAction.SafeRestart));
    }

    [Fact]
    public void UsesSha1ForLegacyAndSha1Suites()
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);

        cryptography.EstablishSession(
            Convert.FromHexString(MaterialCipherHex),
            new KvmCipherSuite(1, 5000));

        Assert.True(cryptography.IsSessionEstablished);
        Assert.Equal(16, cryptography.EncryptInput(new byte[8]).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("XYZ")]
    [InlineData("000102030405060708090A0B0C0D0E0F")]
    public void RejectsInvalidLoginKeys(string value)
    {
        Assert.Throws<FormatException>(() => KvmSessionCryptography.FromLoginKey(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void RejectsInvalidSessionMaterialLengths(int length)
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);

        Assert.Throws<InvalidDataException>(() => cryptography.EstablishSession(
            new byte[length],
            new KvmCipherSuite(3, 10000)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(15, 1)]
    [InlineData(16, 0)]
    [InlineData(16, 17)]
    public void RejectsInvalidEncryptedDataLengths(int encryptedLength, int realLength)
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);
        cryptography.EstablishSession(
            Convert.FromHexString(MaterialCipherHex),
            new KvmCipherSuite(3, 10000));

        Assert.Throws<InvalidDataException>(() =>
            cryptography.DecryptData(new byte[encryptedLength], realLength));
    }

    [Fact]
    public void RejectsSessionOperationsBeforeEstablishmentAndAfterDisposal()
    {
        var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);

        Assert.Throws<InvalidOperationException>(() => cryptography.EncryptInput(new byte[8]));

        cryptography.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cryptography.DeriveConnectAuthenticator(
            "verify",
            new KvmCipherSuite(2, 5000)));
    }
}
