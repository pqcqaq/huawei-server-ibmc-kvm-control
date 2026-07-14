using IbmcKvm.Protocol;

namespace IbmcKvm.Protocol.Tests.Architecture;

public sealed class ProtocolArchitectureTests
{
    [Fact]
    public void ProtocolAssemblyDoesNotReferenceDesktopUiFrameworks()
    {
        var referencedAssemblies = typeof(ProtocolAssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("PresentationCore", referencedAssemblies);
        Assert.DoesNotContain("PresentationFramework", referencedAssemblies);
        Assert.DoesNotContain("WindowsBase", referencedAssemblies);
        Assert.DoesNotContain("System.Windows.Forms", referencedAssemblies);
    }
}
