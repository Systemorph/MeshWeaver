using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// #890, the production half: the executables built under the ROOT <c>Directory.Build.props</c> —
/// represented here by the bake host <c>mw-plugin-test</c>, which compiles every NodeType a CI bake
/// seals — must run with Tiered PGO OFF. Under Tiered PGO the .NET 10 linux-x64 Tier-1 JIT can drop the
/// <c>isinst</c> in Roslyn's inlined <c>SourceMemberContainerTypeSymbol.ContainingType</c>, after which
/// no <c>Emit</c> in that process succeeds (Doc/Architecture/NodeTypeCompilation, "The defect, in one
/// listing").
///
/// <para>This test host inherits <c>test/Directory.Build.props</c>, not the root, so it reads the
/// runtimeconfig the SDK copies beside it from the referenced executables — the files a
/// <c>dotnet mw-plugin-test.dll</c> launch actually reads. Deleting <c>&lt;TieredPGO&gt;false&lt;/TieredPGO&gt;</c>
/// from the root props turns it red; the test tree's own copy of the property cannot keep it green.</para>
/// </summary>
public class BakeHostRunsWithTieredPgoOffTest
{
    [Theory]
    [InlineData("mw-plugin-test")]
    [InlineData("mw-combo-verify")]
    public void TheRootInheritingExecutable_CarriesTieredPgoOff(string host)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{host}.runtimeconfig.json");
        Assert.True(File.Exists(path), $"no runtimeconfig for {host} at {path} — the referenced executable was not copied");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            doc.RootElement.GetProperty("runtimeOptions").TryGetProperty("configProperties", out var props)
            && props.TryGetProperty("System.Runtime.TieredPGO", out var pgo)
            && pgo.ValueKind == JsonValueKind.False,
            $"{path} does not carry \"System.Runtime.TieredPGO\": false — {host} inherits the root "
            + "Directory.Build.props, and without the property it compiles under the #890 miscompile.");
    }
}
