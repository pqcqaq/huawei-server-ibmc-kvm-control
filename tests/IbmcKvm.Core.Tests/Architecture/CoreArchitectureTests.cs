using IbmcKvm.Core;

namespace IbmcKvm.Core.Tests.Architecture;

public sealed class CoreArchitectureTests
{
    [Fact]
    public void CoreAssemblyDoesNotReferenceDesktopUiFrameworks()
    {
        var referencedAssemblies = typeof(CoreAssemblyMarker)
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
