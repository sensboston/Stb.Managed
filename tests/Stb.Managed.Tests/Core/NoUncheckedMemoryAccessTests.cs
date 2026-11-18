using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Stb.Managed.Tests.Core;

/// <summary>
/// Guards the "no unchecked memory access" property of every shipped library: their IL must not reference
/// <c>System.Runtime.CompilerServices.Unsafe</c>, the reference-returning <c>MemoryMarshal</c> helpers, the
/// unchecked vector load/store APIs, or <c>UnsafeAccessor</c>. Every memory access then goes through a
/// bounds-checked span or array.
/// </summary>
public sealed class NoUncheckedMemoryAccessTests
{
    private static readonly HashSet<string> ForbiddenMemoryMarshal = new(StringComparer.Ordinal)
    {
        "GetReference", "GetArrayDataReference", "CreateSpan", "CreateReadOnlySpan", "AsRef",
        "GetNonNullPinnableReference", "CreateReadOnlySpanFromNullTerminated",
    };

    private static readonly HashSet<string> ForbiddenVectorMembers = new(StringComparer.Ordinal)
    {
        "LoadUnsafe", "StoreUnsafe", "LoadAligned", "StoreAligned", "LoadAlignedNonTemporal", "StoreAlignedNonTemporal",
    };

    public static TheoryData<string> LibraryAssemblies()
    {
        var data = new TheoryData<string>();
        foreach (string name in LibraryAssemblyNames())
        {
            data.Add(name);
        }

        return data;
    }

    private static List<string> LibraryAssemblyNames()
    {
        var names = new List<string>();
        foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "Stb.Managed*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Contains(".Tests", StringComparison.Ordinal) || name.Contains(".Oracle", StringComparison.Ordinal))
            {
                continue;
            }

            names.Add(name);
        }

        return names;
    }

    [Fact]
    public void AllLibraryAssembliesAreChecked()
    {
        string[] expected =
        [
            "Stb.Managed.Core", "Stb.Managed.Image", "Stb.Managed.ImageWrite", "Stb.Managed.ImageResize",
            "Stb.Managed.TrueType", "Stb.Managed.RectPack", "Stb.Managed.Dxt", "Stb.Managed.Perlin",
#if !TEST_NETSTANDARD
            "Stb.Managed.Gpu",
#endif
        ];
        var found = LibraryAssemblyNames().ToHashSet(StringComparer.Ordinal);
        foreach (string name in expected)
        {
            Assert.Contains(name, found);
        }
    }

    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void LibraryHasNoUncheckedMemoryAccess(string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new(stream);
        MetadataReader md = pe.GetMetadataReader();

        var violations = new List<string>();
        foreach (MemberReferenceHandle handle in md.MemberReferences)
        {
            MemberReference member = md.GetMemberReference(handle);
            string memberName = md.GetString(member.Name);
            (string ns, string type) = ParentTypeName(md, member.Parent);

            bool forbidden =
                (ns == "System.Runtime.CompilerServices" && type == "Unsafe")
                || (ns == "System.Runtime.InteropServices" && type == "MemoryMarshal" && ForbiddenMemoryMarshal.Contains(memberName))
                || (ns == "System.Runtime.Intrinsics" && type.StartsWith("Vector", StringComparison.Ordinal) && ForbiddenVectorMembers.Contains(memberName))
                || (ns == "System.Runtime.CompilerServices" && type == "UnsafeAccessorAttribute");
            if (forbidden)
            {
                violations.Add($"{ns}.{type}.{memberName}");
            }
        }

        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference tr = md.GetTypeReference(handle);
            if (md.GetString(tr.Namespace) == "System.Runtime.CompilerServices" && md.GetString(tr.Name) == "Unsafe")
            {
                violations.Add("type reference System.Runtime.CompilerServices.Unsafe");
            }
        }

        Assert.True(violations.Count == 0, $"{assemblyName} uses unchecked memory access: {string.Join(", ", violations.Distinct())}");
    }

    private static (string Namespace, string Name) ParentTypeName(MetadataReader md, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                TypeReference tr = md.GetTypeReference((TypeReferenceHandle)parent);
                return (md.GetString(tr.Namespace), md.GetString(tr.Name));
            case HandleKind.TypeDefinition:
                TypeDefinition td = md.GetTypeDefinition((TypeDefinitionHandle)parent);
                return (md.GetString(td.Namespace), md.GetString(td.Name));
            case HandleKind.TypeSpecification:
                // Generic instantiation: decode the open generic type from the signature blob.
                TypeSpecification ts = md.GetTypeSpecification((TypeSpecificationHandle)parent);
                BlobReader blob = md.GetBlobReader(ts.Signature);
                if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
                {
                    blob.ReadSignatureTypeCode();
                    EntityHandle open = blob.ReadTypeHandle();
                    return ParentTypeName(md, open);
                }

                return (string.Empty, string.Empty);
            default:
                return (string.Empty, string.Empty);
        }
    }
}
