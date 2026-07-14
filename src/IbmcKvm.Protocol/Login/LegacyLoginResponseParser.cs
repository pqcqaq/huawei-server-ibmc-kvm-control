using System.Globalization;

namespace IbmcKvm.Protocol.Login;

public static class LegacyLoginResponseParser
{
    private const int MaximumResponseLength = 4096;

    public static IbmcLoginResponse Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Length is 0 or > MaximumResponseLength)
        {
            throw new FormatException("The iBMC login response has an invalid length.");
        }

        var fields = ExtractDelimitedValues(input, '[', ']', 16);
        if (fields.Count == 0 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawError))
        {
            throw new FormatException("The iBMC login response does not contain an error code.");
        }

        var error = MapError(rawError);
        if (error != LoginErrorCode.None)
        {
            return new IbmcLoginResponse
            {
                RawErrorCode = rawError,
                Error = error,
            };
        }

        if (fields.Count < 8)
        {
            throw new FormatException("The successful iBMC login response is incomplete.");
        }

        var extensions = ExtractDelimitedValues(input, '<', '>', 20);
        return new IbmcLoginResponse
        {
            RawErrorCode = rawError,
            Error = LoginErrorCode.None,
            VerifyValue = RequireValue(fields[1], "verify value"),
            DecryptionKey = RequireValue(fields[2].Trim().Trim('"'), "decryption key"),
            Privilege = ParseInteger(fields[3], "privilege"),
            KvmEncrypted = ParseBooleanFlag(fields[4], "KVM encryption"),
            VirtualMediaEncrypted = ParseBooleanFlag(fields[5], "virtual-media encryption"),
            KvmPort = ParsePort(fields[6], "KVM port"),
            VirtualMediaPort = ParsePort(fields[7], "virtual-media port"),
            SerialNumber = extensions.Count > 0 ? extensions[0] : string.Empty,
            ExtendedVerifyValue = extensions.Count > 1 ? extensions[1] : string.Empty,
        };
    }

    private static List<string> ExtractDelimitedValues(string input, char opening, char closing, int maximumCount)
    {
        var values = new List<string>();
        var searchFrom = 0;
        while (searchFrom < input.Length)
        {
            var start = input.IndexOf(opening, searchFrom);
            if (start < 0)
            {
                break;
            }

            var end = input.IndexOf(closing, start + 1);
            if (end < 0)
            {
                throw new FormatException($"The iBMC response has an unterminated '{opening}' field.");
            }

            if (values.Count >= maximumCount)
            {
                throw new FormatException("The iBMC response contains too many fields.");
            }

            values.Add(input[(start + 1)..end]);
            searchFrom = end + 1;
        }

        return values;
    }

    private static LoginErrorCode MapError(int rawError) => rawError switch
    {
        0 => LoginErrorCode.None,
        131 => LoginErrorCode.UserLocked,
        136 => LoginErrorCode.InsufficientPrivilege,
        137 => LoginErrorCode.PasswordExpired,
        144 => LoginErrorCode.LoginRestricted,
        _ => LoginErrorCode.Unknown,
    };

    private static string RequireValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"The iBMC {fieldName} is missing.");
        }

        return value;
    }

    private static int ParseInteger(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException($"The iBMC {fieldName} is invalid.");
        }

        return result;
    }

    private static bool ParseBooleanFlag(string value, string fieldName)
    {
        return value switch
        {
            "0" => false,
            "1" => true,
            _ => throw new FormatException($"The iBMC {fieldName} flag is invalid."),
        };
    }

    private static int ParsePort(string value, string fieldName)
    {
        var port = ParseInteger(value, fieldName);
        if (port is < 1 or > 65535)
        {
            throw new FormatException($"The iBMC {fieldName} is out of range.");
        }

        return port;
    }
}

