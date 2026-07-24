using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class LegacyLoginResponseParserTests
{
    [Fact]
    public void ParseReadsObservedSuccessFieldOrder()
    {
        const string raw = "[0][123456][\"001122aabb\"][4][1][0][8208][8209]<SN123><987654>";

        var response = LegacyLoginResponseParser.Parse(raw);

        Assert.True(response.IsSuccess);
        Assert.Equal("123456", response.VerifyValue);
        Assert.Equal("001122aabb", response.DecryptionKey);
        Assert.Equal(4, response.Privilege);
        Assert.True(response.KvmEncrypted);
        Assert.False(response.VirtualMediaEncrypted);
        Assert.Equal(8208, response.KvmPort);
        Assert.Equal(8209, response.VirtualMediaPort);
        Assert.Equal("SN123", response.SerialNumber);
        Assert.Equal("987654", response.ExtendedVerifyValue);
    }

    [Theory]
    [InlineData("[130]", LoginErrorCode.InvalidCredentials)]
    [InlineData("[144]", LoginErrorCode.LoginRestricted)]
    [InlineData("[136]", LoginErrorCode.InsufficientPrivilege)]
    [InlineData("[137]", LoginErrorCode.PasswordExpired)]
    [InlineData("[131]", LoginErrorCode.UserLocked)]
    [InlineData("[999]", LoginErrorCode.Unknown)]
    public void ParseMapsObservedErrorCodes(string raw, LoginErrorCode expected)
    {
        var response = LegacyLoginResponseParser.Parse(raw);

        Assert.False(response.IsSuccess);
        Assert.Equal(expected, response.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-response")]
    [InlineData("[0][verify]")]
    [InlineData("[0][verify][\"key\"][4][1][0][0][8209]")]
    [InlineData("[0][verify][\"key\"][4][1][0][70000][8209]")]
    public void ParseRejectsMalformedSuccessResponses(string raw)
    {
        Assert.Throws<FormatException>(() => LegacyLoginResponseParser.Parse(raw));
    }
}
