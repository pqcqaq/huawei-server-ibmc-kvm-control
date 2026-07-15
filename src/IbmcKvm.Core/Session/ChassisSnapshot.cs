using System.Collections.Immutable;
using IbmcKvm.Protocol.Session;

namespace IbmcKvm.Core.Session;

public enum KvmBladeSessionMode
{
    Control,
    Monitor,
}

public sealed record ChassisSnapshot(
    DateTimeOffset RefreshedAt,
    ImmutableArray<ChassisBladeState> Blades)
{
    public ChassisBladeState this[byte bladeNumber] =>
        Blades.First(blade => blade.BladeNumber == bladeNumber);
}
