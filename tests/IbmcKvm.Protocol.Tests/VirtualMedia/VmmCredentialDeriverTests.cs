using IbmcKvm.Protocol.Session;
using IbmcKvm.Protocol.VirtualMedia;

namespace IbmcKvm.Protocol.Tests.VirtualMedia;

public sealed class VmmCredentialDeriverTests
{
    [Fact]
    public void MatchesJavaPbkdf2AndAesGoldenVectors()
    {
        var credential = "01234567890123456789"u8.ToArray();
        var salt = Enumerable.Range(0, 16).Select(static value => (byte)(0xA0 + value)).ToArray();

        var derived = VmmCredentialDeriver.Derive(credential, salt, new KvmCipherSuite(2, 5000));
        var complete = derived.SessionId.Concat(derived.DataKey).Concat(derived.InitializationVector).ToArray();
        var ciphertext = VmmCredentialDeriver.Encrypt("managed-vmm-test"u8, derived);

        Assert.Equal(
            "597665F59C6C7AA6C82D97680B1C33B7EE91B3155321137E7675319BACCDD6A5461C411F1ECF832BB26EAAFB9F4ED20F3198442A78F80AD4",
            Convert.ToHexString(complete));
        Assert.Equal("82EAEA934E901B874AB6D9BA7E5AA1E4", Convert.ToHexString(ciphertext));
    }

    [Fact]
    public void WrapsRealLengthOutsideCiphertextAndRoundTripsPartialBlock()
    {
        var derived = VmmCredentialDeriver.Derive(
            "01234567890123456789"u8,
            Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray(),
            new KvmCipherSuite(3, 7000));
        var plaintext = "a partial media block"u8.ToArray();

        var wrapped = VmmCredentialDeriver.WrapEncryptedPayload(plaintext, derived);
        var restored = VmmCredentialDeriver.UnwrapEncryptedPayload(wrapped, derived);

        Assert.Equal(new byte[] { 0, 0, 0, (byte)plaintext.Length }, wrapped[..4]);
        Assert.Equal(4 + 32, wrapped.Length);
        Assert.Equal(plaintext, restored);
    }

    [Theory]
    [InlineData("00000001")]
    [InlineData("0000001182EAEA934E901B874AB6D9BA7E5AA1E4")]
    [InlineData("0000000082EAEA934E901B874AB6D9BA7E5AA1E4")]
    public void RejectsMalformedEncryptedBlocks(string hex)
    {
        var derived = VmmCredentialDeriver.Derive(
            "01234567890123456789"u8,
            new byte[16],
            new KvmCipherSuite(1, 1));

        Assert.Throws<InvalidDataException>(() =>
            VmmCredentialDeriver.UnwrapEncryptedPayload(Convert.FromHexString(hex), derived));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void RejectsUnknownSuites(byte algorithm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VmmCredentialDeriver.Derive(new byte[20], new byte[16], new KvmCipherSuite(algorithm, 1)));
    }
}
