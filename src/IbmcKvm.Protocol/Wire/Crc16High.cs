namespace IbmcKvm.Protocol.Wire;

/// <summary>
/// CRC-16/CCITT high-bit-first implementation used by the iBMC wire protocol.
/// The legacy client calls this variant CRC_16_H (poly 0x1021, init 0).
/// </summary>
public static class Crc16High
{
    public static ushort Compute(ReadOnlySpan<byte> data, ushort initial = 0)
    {
        var crc = initial;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}
