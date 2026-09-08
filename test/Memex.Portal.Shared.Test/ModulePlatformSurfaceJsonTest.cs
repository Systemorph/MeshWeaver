using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The platform surface travels as a document (#3651), and the document must reach the same
/// verdict the live process does.</b>
///
/// <para>A platform roll is held only by a module that provably cannot load on the target. Proving
/// it needs the target's type surface at gate time, and the target is not running anywhere the
/// gate can reach — so the bake, which runs inside the target image, writes
/// <see cref="ModulePlatformSurface.PublishedFileName"/> and the gate reads it back. These pin the
/// two halves: <see cref="ModulePlatformSurface.ToJson"/> writes exactly what
/// <see cref="ModulePlatformSurface.TypesOf"/> answers, <see cref="ModulePlatformSurface.FromJson"/>
/// reads it back into a surface the REAL <see cref="ModulePlatformLink"/> drives to the same
/// verdict — and a document that is not a surface is refused rather than read as an empty one,
/// because an empty surface refuses every module (the platform-prefix rule) and that would be a
/// confidently wrong hold, not a missing measurement.</para>
/// </summary>
public class ModulePlatformSurfaceJsonTest
{
    private const string ContractAssembly = "MeshWeaver.Mesh.Contract";
    private const string Identity = "s3651roundtrip000000000000000000";

    [Fact]
    public void ToJson_ThenFromJson_CarriesTheIdentityAndEveryAssemblysTypes()
    {
        var live = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);

        var json = live.ToJson(Identity);
        var declared = ModulePlatformSurface.FromJson(json);

        Assert.Equal(Identity, declared.Identity);
        Assert.True(declared.IsDeclared);
        Assert.Null(live.Identity);
        Assert.False(live.IsDeclared);

        // The contract assembly is the one every module binds; the document must say exactly what
        // the process says about it — definitions AND forwarded types, nested as Outer+Inner.
        Assert.True(declared.Carries(ContractAssembly));
        var liveTypes = live.TypesOf(ContractAssembly);
        var declaredTypes = declared.TypesOf(ContractAssembly);
        Assert.NotNull(liveTypes);
        Assert.NotNull(declaredTypes);
        Assert.True(liveTypes.SetEquals(declaredTypes),
            $"the document lost or invented types: only-live={string.Join(",", liveTypes.Except(declaredTypes).Take(5))} "
            + $"only-declared={string.Join(",", declaredTypes.Except(liveTypes).Take(5))}");
        Assert.Contains(typeof(MeshNode).FullName!, declaredTypes);
        Assert.Contains(typeof(ModulePlatformSurface).FullName!, declaredTypes);

        // The shape is the documented one, and nothing else.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(Identity, document.RootElement.GetProperty("identity").GetString());
        var assemblies = document.RootElement.GetProperty("assemblies");
        Assert.Equal(JsonValueKind.Object, assemblies.ValueKind);
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.True(assemblies.EnumerateObject().Count() > 10,
            "a surface of the running process names more than a handful of assemblies");
    }

    /// <summary>
    /// 🚨 THE EQUIVALENCE that makes the gate honest: the same module, checked against the live
    /// surface and against the document read back, reaches the same verdict with the same
    /// denominator. A document that changed the answer would make the roll gate and the boot
    /// probe disagree — and the direction that matters is the document being more lenient.
    /// </summary>
    [Fact]
    public void ARealModule_LinksIdenticallyAgainstTheLiveSurfaceAndTheDocument()
    {
        var live = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);
        var declared = ModulePlatformSurface.FromJson(live.ToJson(Identity));
        var module = Emit("MeshWeaver.Test.RoundTripPack", """
            using MeshWeaver.Mesh;
            public static class Views { public static string Show(MeshNode node) => node.Id; }
            """);
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MeshWeaver.Test.RoundTripPack" };

        var onLive = ModulePlatformLink.Check(module, "MeshWeaver.Test.RoundTripPack", closure, live);
        var onDocument = ModulePlatformLink.Check(module, "MeshWeaver.Test.RoundTripPack", closure, declared);

        Assert.Equal(ModuleLinkState.Linkable, onLive.State);
        Assert.Equal(ModuleLinkState.Linkable, onDocument.State);
        Assert.True(onDocument.CheckedTypeReferences > 0, onDocument.Report());
        Assert.Equal(onLive.CheckedTypeReferences, onDocument.CheckedTypeReferences);
        Assert.Equal(onLive.CheckedAssemblies, onDocument.CheckedAssemblies);
        Assert.Equal(onLive.UncheckedAssemblies, onDocument.UncheckedAssemblies);
    }

    /// <summary>
    /// The document describing an OLDER platform — one that does not carry a type this module
    /// binds — refuses the module by name. This is the whole point of publishing the surface: the
    /// gate answers "would it load THERE" for a platform that is not running here.
    /// </summary>
    [Fact]
    public void ADocumentMissingATypeTheModuleBinds_IsUnlinkable_NamingTheType()
    {
        var live = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);
        var older = ModulePlatformSurface.FromJson(Without(live.ToJson(Identity), ContractAssembly, typeof(MeshNode).FullName!));
        var module = Emit("MeshWeaver.Test.NewerPack", """
            using MeshWeaver.Mesh;
            public static class Views { public static string Show(MeshNode node) => node.Id; }
            """);

        var verdict = ModulePlatformLink.Check(
            module, "MeshWeaver.Test.NewerPack",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MeshWeaver.Test.NewerPack" },
            older);

        Assert.Equal(ModuleLinkState.Unlinkable, verdict.State);
        Assert.False(verdict.MayLoad);
        Assert.Contains(verdict.MissingTypes, m => m.StartsWith(typeof(MeshNode).FullName!, StringComparison.Ordinal));
        Assert.Contains(ContractAssembly, verdict.Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 Fails CLOSED on shape. A surface with no assemblies would refuse every module that binds
    /// a <c>MeshWeaver.*</c> assembly ("no such platform assembly") — a hold that looks measured
    /// and is not. So a document that is not the documented shape is an exception the reader turns
    /// into Indeterminate, never a surface.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"identity":"x"}""")]
    [InlineData("""{"identity":"x","assemblies":{}}""")]
    [InlineData("""{"identity":"x","assemblies":{"A":"not-an-array"}}""")]
    [InlineData("""{"identity":"x","assemblies":{"A":[1,2]}}""")]
    [InlineData("not json at all")]
    public void ADocumentThatIsNotASurface_IsRefused_NeverReadAsAnEmptySurface(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ModulePlatformSurface.FromJson(json));
    }

    /// <summary>An assembly the document does not name is not carried — and for a platform-named
    /// one that is the coarse-grained refusal, exactly as against a live surface.</summary>
    [Fact]
    public void AnAssemblyTheDocumentDoesNotName_IsNotCarried()
    {
        var surface = ModulePlatformSurface.FromJson(
            """{"identity":"x","assemblies":{"MeshWeaver.Something":["MeshWeaver.Something.Thing"]}}""");

        Assert.True(surface.Carries("MeshWeaver.Something"));
        Assert.True(surface.Carries("meshweaver.something"));
        Assert.False(surface.Carries(ContractAssembly));
        Assert.Null(surface.TypesOf(ContractAssembly));
        Assert.Equal(["MeshWeaver.Something.Thing"], surface.TypesOf("MeshWeaver.Something")!.ToArray());
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>The same document with one type removed from one assembly — an older platform.</summary>
    internal static string Without(string json, string assemblyName, string typeName)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var assemblies = node["assemblies"]!.AsObject();
        var types = assemblies[assemblyName]!.AsArray();
        var index = types.Select((t, i) => (t, i)).First(x => x.t!.GetValue<string>() == typeName).i;
        types.RemoveAt(index);
        return node.ToJsonString();
    }

    /// <summary>Compiles one source file against this process's reference set.</summary>
    internal static byte[] Emit(string assemblyName, string source)
    {
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
