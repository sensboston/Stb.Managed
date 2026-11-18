using System.Reflection;
using Xunit;

namespace Stb.Managed.Tests.Core;

public sealed class DependencyEnforcementTests
{
    private static readonly string[] LibraryAssemblyNames =
    [
        "Stb.Managed.Core",
        "Stb.Managed.Image",
        "Stb.Managed.ImageWrite",
        "Stb.Managed.ImageResize",
        "Stb.Managed.TrueType",
        "Stb.Managed.RectPack",
        "Stb.Managed.Dxt",
        "Stb.Managed.Perlin",
    ];

    [Fact]
    public void LibraryAssembliesDependOnlyOnCoreAndBcl()
    {
        int verifiedCount = 0;
        foreach (string name in LibraryAssemblyNames)
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            Assert.True(File.Exists(dllPath), $"Expected library assembly at '{dllPath}'.");

            Assembly assembly = Assembly.LoadFrom(dllPath);
            AssemblyName[] references = assembly.GetReferencedAssemblies();
            foreach (AssemblyName reference in references)
            {
                string refName = reference.Name ?? string.Empty;
                bool isAllowed =
                    refName.StartsWith("System.", StringComparison.Ordinal) ||
                    string.Equals(refName, "System", StringComparison.Ordinal) ||
                    string.Equals(refName, "netstandard", StringComparison.Ordinal) ||
                    string.Equals(refName, "mscorlib", StringComparison.Ordinal) ||
                    (!string.Equals(name, "Stb.Managed.Core", StringComparison.Ordinal) &&
                     string.Equals(refName, "Stb.Managed.Core", StringComparison.Ordinal));

                Assert.True(
                    isAllowed,
                    $"Library '{name}' has forbidden runtime dependency '{refName}'.");
            }

            verifiedCount++;
        }

        Assert.Equal(LibraryAssemblyNames.Length, verifiedCount);
    }
}
