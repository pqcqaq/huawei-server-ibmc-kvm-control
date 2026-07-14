using System.Globalization;

namespace IbmcKvm.Protocol.Session;

public readonly record struct SessionVerificationKey(uint Value)
{
    public int WireValue => unchecked((int)Value);

    public static SessionVerificationKey Parse(string value)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException("The iBMC verification value is not an unsigned 32-bit integer.");
        }

        return new SessionVerificationKey(result);
    }
}
