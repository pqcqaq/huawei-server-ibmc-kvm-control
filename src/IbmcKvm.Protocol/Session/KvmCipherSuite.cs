using System.Buffers.Binary;

namespace IbmcKvm.Protocol.Session;

public sealed record KvmCipherSuite(byte Algorithm, int Iterations);

public static class KvmCipherSuiteParser
{
    public static IReadOnlyList<KvmCipherSuite> Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] != 0x43)
        {
            throw new InvalidDataException("The KVM cipher-suite response is malformed.");
        }

        var count = payload[2];
        if (payload.Length != checked(count * 5 + 3))
        {
            throw new InvalidDataException("The KVM cipher-suite count does not match its payload.");
        }

        var result = new KvmCipherSuite[count];
        for (var index = 0; index < count; index++)
        {
            var offset = index * 5 + 3;
            var iterations = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset + 1, 4));
            if (iterations < 1)
            {
                throw new InvalidDataException("The KVM cipher suite has an invalid iteration count.");
            }

            result[index] = new KvmCipherSuite(payload[offset], iterations);
        }

        return result;
    }

    public static KvmCipherSuite SelectPreferred(IReadOnlyList<KvmCipherSuite> suites)
    {
        ArgumentNullException.ThrowIfNull(suites);
        return suites.FirstOrDefault(static suite => suite.Algorithm == 3)
            ?? suites.FirstOrDefault(static suite => suite.Algorithm == 2)
            ?? new KvmCipherSuite(1, 5000);
    }
}
