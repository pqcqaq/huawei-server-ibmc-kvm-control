using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Protocol.Tests.Session;

public sealed class KvmEncryptedPayloadCodecTests
{
    private const string LoginKeyHex =
        "000102030405060708090A0B0C0D0E0F" +
        "101112131415161718191A1B1C1D1E1F";
    private const string MaterialCipherHex =
        "9BE37DD9A6322852855FD577ED0B73A0" +
        "AC624FE33E8DF0407F98AC07BF769C79" +
        "093417894576C00D9ECC936328DB2170";

    [Fact]
    public void EstablishesSessionFromCommand40Response()
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);
        var response = Convert.FromHexString("4001" + MaterialCipherHex);

        KvmEncryptedPayloadCodec.EstablishSession(
            response,
            expectedBladeNumber: 1,
            new KvmCipherSuite(3, 10000),
            cryptography);

        Assert.True(cryptography.IsSessionEstablished);
    }

    [Fact]
    public void LeavesPlainMetadataChunkAndDecryptsDataChunk()
    {
        using var cryptography = CreateEstablishedCryptography();
        var metadata = new byte[17];
        metadata[2] = 7;
        var encrypted = Convert.FromHexString(
            "0001070D47DE27D0AD3BBE09C20BD15981481452");

        Assert.Equal(metadata, KvmEncryptedPayloadCodec.NormalizeVideoChunk(metadata, cryptography));
        Assert.Equal(
            Convert.FromHexString("0001070102030405060708090A0B0C0D"),
            KvmEncryptedPayloadCodec.NormalizeVideoChunk(encrypted, cryptography));
    }

    [Fact]
    public void DecryptsVirtualMediaCredentialAndPortResponses()
    {
        using var cryptography = CreateEstablishedCryptography();
        var credential = Convert.FromHexString(
            "3201" +
            "D95A2BC881C87396CC4D67F2EB189905" +
            "90B17F9464FE2A49AF2B02293D2BF249" +
            "CDEEC792D045FAFB453D9B671E6A2826");
        var port = Convert.FromHexString("36013C364617493F8B2E28EDA852D5C9CE92");

        Assert.Equal(
            Convert.FromHexString(
                "3201" +
                "202122232425262728292A2B2C2D2E2F30313233" +
                "3435363738393A3B3C3D3E3F40414243"),
            KvmEncryptedPayloadCodec.NormalizeVirtualMediaCredential(credential, cryptography));
        Assert.Equal(
            Convert.FromHexString("36013412"),
            KvmEncryptedPayloadCodec.NormalizeVirtualMediaPort(port, cryptography));
    }

    [Theory]
    [InlineData("4002")]
    [InlineData("4101")]
    [InlineData("400100")]
    public void RejectsMalformedSessionMaterialResponses(string hex)
    {
        using var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);

        Assert.Throws<InvalidDataException>(() => KvmEncryptedPayloadCodec.EstablishSession(
            Convert.FromHexString(hex),
            expectedBladeNumber: 1,
            new KvmCipherSuite(3, 10000),
            cryptography));
    }

    [Theory]
    [InlineData("0001")]
    [InlineData("00010700")]
    [InlineData("0001071147DE27D0AD3BBE09C20BD15981481452")]
    public void RejectsMalformedEncryptedVideoChunks(string hex)
    {
        using var cryptography = CreateEstablishedCryptography();

        Assert.Throws<InvalidDataException>(() =>
            KvmEncryptedPayloadCodec.NormalizeVideoChunk(Convert.FromHexString(hex), cryptography));
    }

    private static KvmSessionCryptography CreateEstablishedCryptography()
    {
        var cryptography = KvmSessionCryptography.FromLoginKey(LoginKeyHex);
        cryptography.EstablishSession(
            Convert.FromHexString(MaterialCipherHex),
            new KvmCipherSuite(3, 10000));
        return cryptography;
    }
}
